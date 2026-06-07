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
                context.Result = new JsonResult(new { ExceptionMessage = context.Exception.Message }, _serializerOptions) { StatusCode = 400 };
                context.ExceptionHandled = true;
            }
        }
    }
}
