#if NETCOREAPP
using System;
using System.Text.Json;

namespace Themia.Quartz.Dashboard.Helpers
{
    public class JsonPascalCaseNamingPolicy : JsonNamingPolicy
    {
        public override string ConvertName( string name )
        {
            return name;
        }
    }
}
#endif