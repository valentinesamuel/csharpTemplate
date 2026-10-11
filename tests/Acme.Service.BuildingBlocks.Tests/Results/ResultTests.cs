using Acme.Service.BuildingBlocks.Results;
using Shouldly;

namespace Acme.Service.BuildingBlocks.Tests.Results;

public sealed class ResultTests
{
    private static readonly Error _notFound = Error.NotFound("thing_not_found", "Thing not found.");

    [Fact]
    public void Success_WithValue_ExposesValueAndThrowsOnError()
    {
        Result<int> result = 42;

        result.IsSuccess.ShouldBeTrue();
        result.IsFailure.ShouldBeFalse();
        result.Value.ShouldBe(42);
        Should.Throw<InvalidOperationException>(() => result.Error);
    }

    [Fact]
    public void Failure_WithError_ExposesErrorAndThrowsOnValue()
    {
        Result<int> result = _notFound;

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(_notFound);
        Should.Throw<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void NonGenericResult_SuccessAndFailure_ReportState()
    {
        Result.Success().IsSuccess.ShouldBeTrue();
        Result failure = _notFound;
        failure.IsFailure.ShouldBeTrue();
        failure.Error.Code.ShouldBe("thing_not_found");
    }

    [Fact]
    public void Failure_NullError_Throws()
    {
        Should.Throw<ArgumentNullException>(() => Result.Failure(null!));
    }

    [Theory]
    [InlineData(ErrorType.Validation)]
    [InlineData(ErrorType.Unauthorized)]
    [InlineData(ErrorType.Forbidden)]
    [InlineData(ErrorType.NotFound)]
    [InlineData(ErrorType.Conflict)]
    [InlineData(ErrorType.Unprocessable)]
    [InlineData(ErrorType.TooManyRequests)]
    [InlineData(ErrorType.Unavailable)]
    public void ErrorFactories_EachType_SetCodeMessageAndType(ErrorType type)
    {
        var error = type switch
        {
            ErrorType.Validation => Error.Validation("c", "m"),
            ErrorType.Unauthorized => Error.Unauthorized("c", "m"),
            ErrorType.Forbidden => Error.Forbidden("c", "m"),
            ErrorType.NotFound => Error.NotFound("c", "m"),
            ErrorType.Conflict => Error.Conflict("c", "m"),
            ErrorType.Unprocessable => Error.Unprocessable("c", "m"),
            ErrorType.TooManyRequests => Error.TooManyRequests("c", "m"),
            ErrorType.Unavailable => Error.Unavailable("c", "m"),
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };

        error.ShouldBe(new Error("c", "m", type));
    }

    [Fact]
    public void ErrorType_Values_AreExactlyTheEightPlannedTypes()
    {
        Enum.GetNames<ErrorType>().ShouldBe(
            ["Validation", "Unauthorized", "Forbidden", "NotFound", "Conflict", "Unprocessable", "TooManyRequests", "Unavailable"]);
    }
}
