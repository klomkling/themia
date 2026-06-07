using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using System.Text.Json;

namespace Themia.Quartz.Dashboard.Helpers
{
    public class JsonErrorResponseAttribute : ActionFilterAttribute
    {
        // PascalCase (PropertyNamingPolicy = null is the default) — templates/JS expect PascalCase.
        private static readonly JsonSerializerOptions _serializerOptions = new JsonSerializerOptions();

        public override void OnActionExecuted(ActionExecutedContext context)
        {
            if (context.Exception != null)
            {
                // ContentResult (not JsonResult) so the response doesn't depend on the host's MVC JSON
                // stack — a host using AddNewtonsoftJson() would otherwise throw on our JsonSerializerOptions.
                context.Result = new ContentResult
                {
                    Content = JsonSerializer.Serialize(new { ExceptionMessage = context.Exception.Message }, _serializerOptions),
                    ContentType = "application/json",
                    StatusCode = 400,
                };
                context.ExceptionHandled = true;
            }
        }
    }
}
