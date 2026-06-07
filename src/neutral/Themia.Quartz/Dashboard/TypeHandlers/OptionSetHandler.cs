using System.Collections.Generic;

namespace Themia.Quartz.Dashboard.TypeHandlers
{
    [EmbeddedTypeHandlerResources(nameof(OptionSetHandler))]
    public abstract class OptionSetHandler : TypeHandlerBase
    {
        /// <summary>
        /// Return Key->DisplayName
        /// </summary>
        public abstract KeyValuePair<string, string>[] GetItems();
    }
}
