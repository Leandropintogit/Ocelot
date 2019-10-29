using Consul;
using Ocelot.Infrastructure.Extensions;
using Ocelot.Logging;
using Ocelot.ServiceDiscovery.Providers;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
using Ocelot.Values;

namespace Ocelot.Provider.Consul
{
    public class Consul : IServiceDiscoveryProvider
    {
        private readonly ConsulRegistryConfiguration _config;
        private readonly IOcelotLogger _logger;
        private readonly IConsulClient _consul;
        private const string VersionPrefix = "version-";

        public Consul(ConsulRegistryConfiguration config, IOcelotLoggerFactory factory, IConsulClientFactory clientFactory)
        {
            _logger = factory.CreateLogger<Consul>();
            _config = config;
            _consul = clientFactory.Get(_config);
        }

        public async Task<List<Service>> Get()
        {
            var queryResult = await _consul.Health.Service(_config.KeyOfServiceInConsul, string.Empty, true);
            var nodes = await _consul.Catalog.Nodes();

            var services = new List<Service>();

            foreach (var serviceEntry in queryResult.Response)
            {
                if (IsValid(serviceEntry))
                {
                    services.Add(BuildService(serviceEntry.Service, nodes.Response?.FirstOrDefault(n => n.Address == serviceEntry.Service.Address)));
                }
                else
                {
                    _logger.LogWarning($"Unable to use service Address: {serviceEntry.Service.Address} and Port: {serviceEntry.Service.Port} as it is invalid. Address must contain host only e.g. localhost and port must be greater than 0");
                }
            }

            return services.ToList();
        }

        private static Service BuildService(AgentService agentService, Node serviceNode)
        {
            var serviceHostAndPort = new ServiceHostAndPort(agentService.Address, agentService.Port)
        {
                DownstreamHostName = serviceNode?.Name
            };

            return new Service(agentService.Service, serviceHostAndPort, agentService.ID, GetVersionFromStrings(agentService.Tags),
                agentService.Tags ?? Enumerable.Empty<string>());
        }

        private static bool IsValid(ServiceEntry serviceEntry)
            {
            return !string.IsNullOrEmpty(serviceEntry.Service.Address) && !serviceEntry.Service.Address.Contains("http://")
                && !serviceEntry.Service.Address.Contains("https://") && serviceEntry.Service.Port > 0;
        }

        private static string GetVersionFromStrings(IEnumerable<string> strings)
        {
            return strings?.FirstOrDefault(x => x.StartsWith(VersionPrefix, StringComparison.Ordinal))
                .TrimStart(VersionPrefix);
        }
    }
}
