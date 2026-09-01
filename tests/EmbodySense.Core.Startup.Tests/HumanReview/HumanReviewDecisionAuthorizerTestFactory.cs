using System.Reflection;
using ApplicationDecisionAuthorizer = EmbodySense.Core.Application.HumanReview.IHumanReviewDecisionAuthorizer;
using ApplicationTrustedClock = EmbodySense.Core.Application.HumanReview.IHumanReviewTrustedClock;
using EmbodySense.Core.Startup.HumanReview;
using EmbodySense.Core.Startup.HumanReview.Models;

namespace EmbodySense.Core.Startup.Tests.HumanReview;

internal static class HumanReviewDecisionAuthorizerTestFactory
{
    public static ApplicationDecisionAuthorizer Create(IHumanReviewDecisionAuthorizationProvider? provider)
    {
        var authorizerType = typeof(HumanReviewRuntimeFacade).Assembly.GetType("EmbodySense.Core.Startup.HumanReview.ServerOwnedHumanReviewDecisionAuthorizer", throwOnError: true)!;
        var constructor = authorizerType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single(item => item.GetParameters() is [{ ParameterType: var parameterType }] && parameterType == typeof(IHumanReviewDecisionAuthorizationProvider));
        return (ApplicationDecisionAuthorizer)constructor.Invoke([provider]);
    }

    public static ApplicationTrustedClock CreateTrustedClock(TimeProvider timeProvider)
    {
        var clockType = typeof(HumanReviewRuntimeFacade).Assembly.GetType("EmbodySense.Core.Startup.HumanReview.TimeProviderHumanReviewTrustedClock", throwOnError: true)!;
        var constructor = clockType.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single(item => item.GetParameters() is [{ ParameterType: var parameterType }] && parameterType == typeof(TimeProvider));
        return (ApplicationTrustedClock)constructor.Invoke([timeProvider]);
    }
}
