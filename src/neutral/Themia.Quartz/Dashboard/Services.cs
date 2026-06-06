using HandlebarsDotNet;
using Quartz;
using Themia.Quartz.Dashboard.Helpers;

namespace Themia.Quartz.Dashboard
{
    public class Services
    {
        internal const string ContextKey = "Themia.Quartz.Dashboard.services";

        public ThemiaQuartzOptions Options { get; set; }

        public ViewEngine ViewEngine { get; set; }

        public IHandlebars Handlebars { get; set; }

        public TypeHandlerService TypeHandlers { get; set; }

        public IScheduler Scheduler { get; set; }

        internal Cache Cache { get; private set; }

        public static Services Create(ThemiaQuartzOptions options)
        {
            var handlebarsConfiguration = new HandlebarsConfiguration()
            {
                FileSystem = ViewFileSystemFactory.Create(options),
                ThrowOnUnresolvedBindingExpression = true,
            };

            var services = new Services()
            {
                Options = options,
                Scheduler = options.Scheduler,
                Handlebars = HandlebarsDotNet.Handlebars.Create(handlebarsConfiguration),
            };

            HandlebarsHelpers.Register(services);

            services.ViewEngine = new ViewEngine(services);
            services.TypeHandlers = new TypeHandlerService(services);
            services.Cache = new Cache(services);

            return services;
        }
    }
}
