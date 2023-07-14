using GameHook.Domain.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;

namespace GameHook.WebAPI.Controllers
{
    public record UpdateMemoryModel(uint Address, byte[] Bytes);

    [ApiController]
    [Produces("application/json")]
    [Consumes("application/json")]
    [Route("driver")]
    public class DriverController : Controller
    {
        private readonly IGameHookDriver _driver;
        private readonly IGameHookInstance _instance;

        public DriverController(IGameHookDriver gameHookDriver, IGameHookInstance instance)
        {
            _driver = gameHookDriver;
            _instance = instance;
        }

        [HttpPut("memory")]
        [SwaggerOperation("Write bytes back to the driver manually.")]
        public async Task<IActionResult> WriteMemory(UpdateMemoryModel model)
        {
            await _driver.WriteBytes(model.Address, model.Bytes);

            return Ok();
        }
    }
}
