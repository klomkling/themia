using Microsoft.AspNetCore.Mvc;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Themia.Quartz.Dashboard.Helpers;
using Themia.Quartz.Dashboard.Json;
using Themia.Quartz.Dashboard.TypeHandlers;

namespace Themia.Quartz.Dashboard.Controllers
{
    public class JobDataMapController : PageControllerBase
    {
        [HttpPost, JsonErrorResponse]
        public async Task<IActionResult> ChangeType()
        {
            var formData = await Request.GetFormData();

            TypeHandlerBase selectedType, targetType;
            try
            {
                selectedType = Services.TypeHandlers.Deserialize((string)formData.First(x => x.Key == "selected-type").Value);
                targetType = Services.TypeHandlers.Deserialize((string)formData.First(x => x.Key == "target-type").Value);
            }
            catch (UnknownTypeHandlerException)
            {
                // A client token referencing a missing/unknown TypeId maps to an empty BadRequest.
                // All other exceptions (malformed payload, bind failure, etc.) propagate to the
                // [JsonErrorResponse] filter, which returns 400 with the exception message.
                return new BadRequestResult();
            }

            var dataMapForm = (await formData.GetJobDataMapForm(includeRowIndex: false)).SingleOrDefault(); // expected single row

            var oldValue = selectedType.ConvertFrom(dataMapForm);

            // phase 1: direct conversion
            var newValue = targetType.ConvertFrom(oldValue);

            if (oldValue != null && newValue == null) // if phase 1 failed
            {
                // phase 2: conversion using invariant string
                var str = selectedType.ConvertToString(oldValue);
                newValue = targetType.ConvertFrom(str);
            }

            return Html(targetType.RenderView(Services, newValue));
        }

        [HttpGet, ActionName("TypeHandlers.js")]
        public IActionResult TypeHandlersScript()
        {
            var etag = Services.TypeHandlers.LastModified.ETag();

            if (etag.Equals(GetETag()))
                return NotModified();

            var execStubBuilder = new StringBuilder();
            execStubBuilder.AppendLine();
            foreach (var func in new[] { "init" })
                execStubBuilder.AppendLine(string.Format("if (f === '{0}' && {0} !== 'undefined') {{ {0}.call(this); }}", func));

            var execStub = execStubBuilder.ToString();

            var scripts = Services.TypeHandlers.GetScripts();
            var sb = new StringBuilder("var $typeHandlerScripts = {");
            var first = true;
            foreach (var kvp in scripts)
            {
                if (!first) sb.Append(',');
                first = false;
                // Raw-injected into the .js object literal → RawInject (literal chars), like every
                // other raw-injection site. TypeIds are CLR FullNames so this is byte-identical today.
                sb.Append(JsonSerializer.Serialize(kvp.Key, DashboardJsonOptions.RawInject));
                sb.Append(":function(f) {");
                sb.Append(kvp.Value);
                sb.Append(execStub);
                sb.Append('}');
            }
            sb.Append("};");

            return TextFile(sb.ToString(), "application/javascript", Services.TypeHandlers.LastModified, etag);
        }
    }
}
