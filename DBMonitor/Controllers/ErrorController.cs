using Microsoft.AspNetCore.Mvc;

namespace DBMonitor.Controllers;

public class ErrorController : Controller
{
    [Route("Error/{statusCode}")]
    public IActionResult Handle(int statusCode)
    {
        ViewData["StatusCode"] = statusCode;
        return statusCode switch
        {
            404 => View("404"),
            403 => View("403"),
            _   => View("500"),
        };
    }
}
