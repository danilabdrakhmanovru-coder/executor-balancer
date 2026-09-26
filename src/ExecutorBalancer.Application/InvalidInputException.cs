namespace ExecutorBalancer.Application;

public sealed class InvalidInputException(IDictionary<string, string[]> errors)
    : Exception("Некорректные данные")
{
    public IDictionary<string, string[]> Errors { get; } = errors;
}
