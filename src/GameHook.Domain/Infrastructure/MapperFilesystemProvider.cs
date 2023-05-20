﻿using GameHook.Domain.DTOs;
using GameHook.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using System.Text;

namespace GameHook.Domain.Infrastructure
{
    public static class MapperFilesystemHelper
    {
        public static IEnumerable<FileInfo> GetFilesByExtensions(this DirectoryInfo dir, params string[] extensions)
        {
            if (extensions == null) throw new ArgumentNullException("extensions");

            IEnumerable<FileInfo> files = dir.EnumerateFiles("*", SearchOption.AllDirectories);
            return files.Where(f => extensions.Contains(f.Extension));
        }
    }
    
    public class MapperFilesystemProvider : IMapperFilesystemProvider
    {
        private ILogger<MapperFilesystemProvider> Logger { get; }
        private const string Salt = "a1b3d4e8-81cc-4771-a564-f90c669d952e";

        public string OfficialMapperFolder { get; } = Path.Combine(BuildEnvironment.ConfigurationDirectory, "Mappers");
        public string? CustomMapperFolder { get; }

        public IEnumerable<MapperFilesystemDTO> MapperFiles { get; private set; } = new List<MapperFilesystemDTO>();

        public MapperFilesystemProvider(ILogger<MapperFilesystemProvider> logger)
        {
            Logger = logger;

            CustomMapperFolder = GetCustomMappersFolder();

            RefreshMapperFiles();
        }

        public void RefreshMapperFiles()
        {
            MapperFiles = GetAllMapperFiles();
        }

        private string? GetCustomMappersFolder()
        {
            var processPath = Path.GetDirectoryName(Environment.ProcessPath);
            if (processPath == null)
            {
                Logger.LogWarning("Unable to determine the process path for executable. Cannot determine the custom mapper folder.");
                return null;
            }

            var folder = Path.Combine(processPath, "Mappers");
            if (Directory.Exists(folder) == false)
            {
                return null;
            }

            return folder;
        }

        private string MD5(string x)
        {
            using var provider = System.Security.Cryptography.MD5.Create();
            StringBuilder builder = new StringBuilder();

            foreach (byte b in provider.ComputeHash(Encoding.UTF8.GetBytes(x + Salt)))
                builder.Append(b.ToString("x2").ToLower());

            return builder.ToString();
        }

        private string GetRelativePath(string filePath)
        {
            return filePath.Replace(OfficialMapperFolder, string.Empty);
        }

        /// <summary>
        /// We replace the base path with an empty string
        /// as to not expose the absolute path of the filesystem.
        /// </summary>
        /// <returns></returns>
        private IEnumerable<MapperFilesystemDTO> GetAllMapperFiles()
        {
            var mappers = new DirectoryInfo(OfficialMapperFolder)
                .GetFilesByExtensions(".xml", ".yml")
                .Select(x => new MapperFilesystemDTO()
                {
                    Id = MD5(x.FullName),
                    Type = MapperFilesystemTypes.Official,
                    AbsolutePath = x.FullName,
                    DisplayName = $"official:\\{GetRelativePath(x.FullName)}"
                })
                .ToList();

            if (CustomMapperFolder != null)
            {
                var customMappers = new DirectoryInfo(CustomMapperFolder)
                    .GetFilesByExtensions(".xml", ".yml")
                    .Select(x => new MapperFilesystemDTO()
                    {
                        Id = MD5(x.FullName),
                        Type = MapperFilesystemTypes.Official,
                        AbsolutePath = x.FullName,
                        DisplayName = $"custom:\\{GetRelativePath(x.FullName)}"
                    })
                    .ToList();

                mappers.AddRange(customMappers);
            }

            return mappers;
        }
    }
}
