using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using System.Text.Json;
using Themia.Quartz.Dashboard.Json;

namespace Themia.Quartz.Dashboard.Helpers
{
    public class JsonErrorResponseAttribute : ActionFilterAttribute
    {
        public override void OnActionExecuted(ActionExecutedContext context)
        {
            if (context.Exception != null)
            {
                // ContentResult (not JsonResult) so the response doesn't depend on the host's MVC JSON
                // stack — a host using AddNewtonsoftJson() would otherwise throw on our JsonSerializerOptions.
                context.Result = new ContentResult
                {
                    Content = JsonSerializer.Serialize(new { ExceptionMessage = context.Exception.Message }, DashboardJsonOptions.Default),
                    ContentType = "application/json",
                    StatusCode = 400,
                };
                context.ExceptionHandled = true;
            }
        }
    }
}
