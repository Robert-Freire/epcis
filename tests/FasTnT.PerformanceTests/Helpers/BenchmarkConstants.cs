using FasTnT.Application.Services.Notifications;
using FasTnT.Application.Services.Users;
using FasTnT.Domain;
using FasTnT.Domain.Model;
using FasTnT.Domain.Model.Queries;
using FasTnT.Domain.Model.Subscriptions;
using Microsoft.Extensions.Options;

namespace FasTnT.PerformanceTests.Helpers;

public class TestCurrentUser : ICurrentUser
{
    public string UserId => "benchmark_user";
    public string UserName => "benchmark";
    public IEnumerable<QueryParameter> DefaultQueryParameters => Enumerable.Empty<QueryParameter>();
}

public class NoOpEventNotifier : IEventNotifier
{
    public void RequestCaptured(Request request) { }
    public void SubscriptionRegistered(Subscription subscription) { }
    public void SubscriptionRemoved(Subscription subscription) { }
}

public static class BenchmarkConstants
{
    public static IOptions<Constants> Create(int maxEventsPerCall = 10000)
    {
        return Options.Create(new Constants
        {
            MaxEventsCapturePerCall = maxEventsPerCall,
            MaxEventsReturnedInQuery = 20000,
            CaptureSizeLimit = 1024
        });
    }

    public static IOptions<Constants> CreateForQueries(int maxEventsReturned = 60000)
    {
        return Options.Create(new Constants
        {
            MaxEventsCapturePerCall = 10000,
            MaxEventsReturnedInQuery = maxEventsReturned,
            CaptureSizeLimit = 1024
        });
    }
}
