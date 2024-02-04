using GameHook.Domain;
using GameHook.Domain.Implementations;
using GameHook.Domain.Interfaces;
using Jint;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

namespace GameHook.Application
{
    public class GameHookInstance : IGameHookInstance
    {
        private readonly ILogger<GameHookInstance> _logger;
        private readonly AppSettings _appSettings;
        private ScriptConsole ScriptConsoleAdapter { get; }
        private CancellationTokenSource? ReadLoopToken { get; set; }
        private IMapperFilesystemProvider MapperFilesystemProvider { get; }
        private IEnumerable<MemoryAddressBlock>? BlocksToRead { get; set; }
        public List<IClientNotifier> ClientNotifiers { get; }
        public bool Initalized { get; private set; }
        public IGameHookDriver? Driver { get; private set; }
        public IGameHookMapper? Mapper { get; private set; }
        public IPlatformOptions? PlatformOptions { get; private set; }
        public IMemoryManager MemoryContainerManager { get; private set; }
        public Dictionary<string, object?> State { get; private set; }
        public Dictionary<string, object?> Variables { get; private set; }
        public Engine? GlobalScriptEngine1 { get; set; }
        public Engine? GlobalScriptEngine2 { get; set; }
        public Engine? GlobalScriptEngine3 { get; set; }

        private const uint MACOS_MAX_MEMORY_ADDRESS_BLOCK_SIZE = 2048;
#if DEBUG
        private bool DebugOutputMemoryLayoutToFilesystem { get; set; } = false;
#endif

        public GameHookInstance(ILogger<GameHookInstance> logger, AppSettings appSettings, ScriptConsole scriptConsoleAdapter, IMapperFilesystemProvider provider, IEnumerable<IClientNotifier> clientNotifiers)
        {
            _logger = logger;
            _appSettings = appSettings;
            ScriptConsoleAdapter = scriptConsoleAdapter;
            MapperFilesystemProvider = provider;
            ClientNotifiers = clientNotifiers.ToList();

            MemoryContainerManager = new MemoryManager();
            State = [];
            Variables = [];
        }

        private async Task ResetState()
        {
            if (ReadLoopToken != null && ReadLoopToken.Token.CanBeCanceled)
            {
                ReadLoopToken.Cancel();
            }

            Initalized = false;
            ReadLoopToken = null;

            Driver = null;
            Mapper = null;
            PlatformOptions = null;
            BlocksToRead = null;

            GlobalScriptEngine1 = null;
            GlobalScriptEngine2 = null;
            GlobalScriptEngine3 = null;

            State = [];
            Variables = [];

            await ClientNotifiers.ForEachAsync(async x => await x.SendInstanceReset());
        }

        private async Task ReadLoop()
        {
            if (Driver == null) throw new Exception("Driver is null.");

            if (_appSettings.SHOW_READ_LOOP_STATISTICS)
            {
                var stopwatch = new Stopwatch();

                while (ReadLoopToken != null && ReadLoopToken.IsCancellationRequested == false)
                {
                    try
                    {
                        stopwatch.Restart();

                        await Read();

                        stopwatch.Stop();

                        _logger.LogInformation($"Instance loop took {stopwatch.ElapsedMilliseconds} ms.");

                        await Task.Delay(Driver.DelayMsBetweenReads);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "An error occured when read looping the mapper.");

                        await ResetState();
                    }
                }
            }
            else
            {
                while (ReadLoopToken != null && ReadLoopToken.IsCancellationRequested == false)
                {
                    try
                    {
                        await Read();

                        await Task.Delay(Driver.DelayMsBetweenReads);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "An error occured when read looping the mapper.");

                        await ResetState();
                    }
                }
            }
        }

        private async Task Read()
        {
            if (Driver == null) throw new Exception("Driver is null.");
            if (PlatformOptions == null) throw new Exception("Platform options are null.");
            if (Mapper == null) throw new Exception("Mapper is null.");
            if (BlocksToRead == null) throw new Exception("BlocksToRead is null.");

            var driverResult = await Driver.ReadBytes(BlocksToRead);

            foreach (var result in driverResult)
            {
                MemoryContainerManager.DefaultNamespace.Fill(result.Key, result.Value);
            }

#if DEBUG
            if (MemoryContainerManager is not IStaticMemoryDriver && DebugOutputMemoryLayoutToFilesystem)
            {
                var memoryContainerPath = Path.GetFullPath(Path.Combine(BuildEnvironment.BinaryDirectoryGameHookFilePath, "..", "..", "..", "..", "..", "..", "GameHook.IntegrationTests", "Data", $"{Mapper.Metadata.Id}-0.json"));

                File.WriteAllText(memoryContainerPath, JsonSerializer.Serialize(driverResult));
                DebugOutputMemoryLayoutToFilesystem = false;
            }
#endif

            // Setup at start of loop
            foreach (var property in Mapper.Properties.Values)
            {
                property.FieldsChanged.Clear();
            }

            // Preprocessor
            if (Mapper.HasGlobalPreprocessor)
            {
                if (GlobalScriptEngine1 == null) throw new Exception("GlobalScriptEngine1 is null.");

                if (GlobalScriptEngine1.Invoke("preprocessor").ToObject() as bool? == false)
                {
                    // The preprocessor returned false, which means we do not want to run anything this loop.
                    return;
                }
            }

            // Processor
            foreach (var property in Mapper.Properties.Values)
            {
                try
                {
                    property.ProcessLoop(MemoryContainerManager);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Property {property.Path} failed to run processor.");
                    throw new PropertyProcessException($"Property {property.Path} failed to run processor.", ex);
                }
            }

            // Postprocessor
            if (Mapper.HasGlobalPostprocessor)
            {
                if (GlobalScriptEngine1 == null) throw new Exception("GlobalScriptEngine1 is null.");

                GlobalScriptEngine1.Invoke("postprocessor");
            }

            // Fields Changed
            var propertiesChanged = Mapper.Properties.Values.Where(x => x.FieldsChanged.Any()).ToArray();
            if (propertiesChanged.Length > 0)
            {
                try
                {
                    foreach (var notifier in ClientNotifiers)
                    {
                        await notifier.SendPropertiesChanged(propertiesChanged);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Could not send {propertiesChanged.Length} property change events.");
                    throw new PropertyProcessException($"Could not send {propertiesChanged.Length} property change events.", ex);
                }
            }
        }

        public async Task Load(IGameHookDriver driver, string mapperId)
        {
            try
            {
                await ResetState();

                _logger.LogDebug("Creating GameHook mapper instance...");

                Driver = driver;

                await Driver.EstablishConnection();

                // Load the mapper file.
                if (string.IsNullOrEmpty(mapperId))
                {
                    throw new ArgumentException("ID was NULL or empty.", nameof(mapperId));
                }

                // Get the file path from the filesystem provider.
                var mapperFile = MapperFilesystemProvider.MapperFiles.SingleOrDefault(x => x.Id == mapperId) ??
                                 throw new Exception($"Unable to determine a mapper with the ID of {mapperId}.");

                if (File.Exists(mapperFile.AbsolutePath) == false)
                {
                    throw new FileNotFoundException($"File was not found in the {mapperFile.Type} mapper folder.", mapperFile.DisplayName);
                }

                var mapperContents = await File.ReadAllTextAsync(mapperFile.AbsolutePath);
                if (mapperFile.AbsolutePath.EndsWith(".xml"))
                {
                    var scriptContentsAbsolutePath = mapperFile.AbsolutePath.Replace(".xml", ".js");
                    string? scriptContents = null;

                    if (File.Exists(scriptContentsAbsolutePath))
                    {
                        scriptContents = await File.ReadAllTextAsync(scriptContentsAbsolutePath);

                        // This is a bit nasty, but it's needed for the non-module script to load within Jint.
                        var pattern = @"export\s*\{[^}]*\};"; // Regex pattern to match 'export { };' with any content inside
                        scriptContents = Regex.Replace(scriptContents, pattern, string.Empty);
                    }

                    Mapper = GameHookMapperXmlFactory.LoadMapperFromFile(this, mapperContents, scriptContents);
                }
                else
                {
                    throw new Exception($"Invalid file extension for mapper.");
                }

                PlatformOptions = Mapper.Metadata.GamePlatform switch
                {
                    "NES" => new NES_PlatformOptions(),
                    "SNES" => new SNES_PlatformOptions(),
                    "GB" => new GB_PlatformOptions(),
                    "GBC" => new GBC_PlatformOptions(),
                    "GBA" => new GBA_PlatformOptions(),
                    "PSX" => new PSX_PlatformOptions(),
                    "NDS" => new NDS_PlatformOptions(),
                    _ => throw new Exception($"Unknown game platform {Mapper.Metadata.GamePlatform}.")
                };

                var script = Engine.PrepareScript(Mapper.GlobalScript ?? string.Empty, strict: true);
                var engineOptions = new Options { Strict = true, StringCompilationAllowed = false };

                GlobalScriptEngine1 = new Engine(engineOptions)
                    .SetValue("__console", ScriptConsoleAdapter)
                    .SetValue("__state", State)
                    .SetValue("__variables", Variables)
                    .SetValue("__mapper", Mapper)
                    .SetValue("__memory", MemoryContainerManager)
                    .SetValue("__driver", Driver)
                    .Execute(script);

                GlobalScriptEngine2 = new Engine(engineOptions)
                    .SetValue("__console", ScriptConsoleAdapter)
                    .SetValue("__state", State)
                    .SetValue("__variables", Variables)
                    .SetValue("__mapper", Mapper)
                    .SetValue("__memory", MemoryContainerManager)
                    .SetValue("__driver", Driver)
                    .Execute(script);

                GlobalScriptEngine3 = new Engine(engineOptions)
                    .SetValue("__console", ScriptConsoleAdapter)
                    .SetValue("__state", State)
                    .SetValue("__variables", Variables)
                    .SetValue("__mapper", Mapper)
                    .SetValue("__memory", MemoryContainerManager)
                    .SetValue("__driver", Driver)
                    .Execute(script);

                // Calculate the blocks to read from the mapper memory addresses.
                BlocksToRead = Mapper.Memory.ReadRanges.Select(x => new MemoryAddressBlock($"Range {x.Start}", x.Start, x.End)).ToArray();

                if (BlocksToRead.Any())
                {
                    _logger.LogInformation($"Using {BlocksToRead.Count()} memory read ranges from mapper.");
                }
                else
                {
                    _logger.LogInformation("Using default driver memory read ranges.");

                    BlocksToRead = PlatformOptions.Ranges.ToList();
                }

                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    // We limit the length of the blocks in masOS due to the fact that the it has a lower dgram packet size
                    BlocksToRead = SplitMemoryAddressBlocks(BlocksToRead.ToList(), MACOS_MAX_MEMORY_ADDRESS_BLOCK_SIZE);
                }

                await Read();

                Initalized = true;

                await ClientNotifiers.ForEachAsync(async x => await x.SendMapperLoaded(Mapper));

                // Start the read loop once successfully running once.
                ReadLoopToken = new CancellationTokenSource();
                _ = Task.Run(ReadLoop, ReadLoopToken.Token);

                _logger.LogInformation($"Loaded mapper for {Mapper.Metadata.GameName} ({Mapper.Metadata.Id}).");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occured when loading the mapper.");

                await ResetState();

                throw;
            }
        }

        private List<MemoryAddressBlock> SplitMemoryAddressBlocks(List<MemoryAddressBlock> blocks, uint blockMaxSize)
        {
            var splittedBlocks = new List<MemoryAddressBlock>();

            for (int i = 0; i < blocks.Count(); i++)
            {
                MemoryAddressBlock memBlock = blocks[i];
                int rangeLength = Convert.ToInt32(memBlock.EndingAddress) - Convert.ToInt32(memBlock.StartingAddress);
                if (rangeLength > 0)
                {
                    if (rangeLength > blockMaxSize)
                    {
                        uint numberOfBlocks = (uint)Math.Ceiling(rangeLength / Convert.ToDouble(blockMaxSize));
                        for (uint j = 0; j < numberOfBlocks; j++)
                        {
                            MemoryAddress startingAddress = memBlock.StartingAddress + blockMaxSize * j + j;
                            MemoryAddress endingAddress = Math.Min(startingAddress + blockMaxSize, memBlock.EndingAddress);
                            var block = new MemoryAddressBlock($"{memBlock.Name} {j}", startingAddress, endingAddress);
                            splittedBlocks.Add(block);
                        }
                    }
                    else
                    {
                        splittedBlocks.Add(memBlock);
                    }
                }
            }
            return splittedBlocks;
        }

        public object? EvalulateExpression_Type1(string function, object? value)
        {
            if (GlobalScriptEngine2 == null) throw new Exception("GlobalScriptEngine2 is null.");

            try
            {
                return GlobalScriptEngine2.SetValue("x", value).Evaluate(function).ToObject();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Javascript EvalulateExpression_Type1 evalulate engine exception.");

                throw;
            }
        }

        public bool? ExecuteFunction_Type1(string? function, IGameHookProperty property)
        {
            if (string.IsNullOrEmpty(function)) return null;
            if (GlobalScriptEngine3 == null) throw new Exception("GlobalScriptEngine3 is null.");

            try
            {
                return (bool?)GlobalScriptEngine3.Invoke(function, property).ToObject();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Javascript ExecuteFunction_Type1 evalulate engine exception.");

                throw;
            }
        }

        private static byte ConvertDoubleToByte(double value)
        {
            // Round the double value to the nearest integer and cast it to byte.
            byte byteValue = (byte)Math.Round(value);

            // Ensure the byte value is within the valid byte range (0 to 255).
            if (byteValue < 0 || byteValue > 255)
            {
                throw new ArgumentException("Double value cannot be converted to a byte because it's out of range.");
            }

            return byteValue;
        }

        public byte[] ExecuteFunction_Type2(string function, byte[] bytes, IGameHookProperty property)
        {
            if (GlobalScriptEngine3 == null) throw new Exception("GlobalScriptEngine3 is null.");

            try
            {
                var result = GlobalScriptEngine3.Invoke(function, bytes, property).ToObject() as object[] ??
                    throw new Exception($"function {function} must return a byte array.");

                return result.Select(x => ConvertDoubleToByte((double)x)).ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Javascript ExecuteFunction_Type2 evalulate engine exception.");

                throw;
            }
        }
    }
}
