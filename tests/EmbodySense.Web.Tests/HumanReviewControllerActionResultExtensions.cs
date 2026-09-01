using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace EmbodySense.Web.Tests;

internal static class HumanReviewControllerActionResultExtensions
{
    public static int? ResultStatusCode<T>(this ActionResult<T> result)
        => result.Result switch
        {
            ObjectResult objectResult => objectResult.StatusCode,
            StatusCodeResult statusCodeResult => statusCodeResult.StatusCode,
            EmptyResult => StatusCodes.Status204NoContent,
            null => StatusCodes.Status200OK,
            _ => null,
        };

    public static T? ObjectValue<T>(this ActionResult<T> result)
        => (result.Result as ObjectResult)?.Value is T value ? value : default;

    public static object? RawObjectValue<T>(this ActionResult<T> result)
        => (result.Result as ObjectResult)?.Value;
}
