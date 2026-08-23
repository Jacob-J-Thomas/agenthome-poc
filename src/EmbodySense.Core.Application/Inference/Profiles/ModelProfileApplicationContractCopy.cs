namespace EmbodySense.Core.Application.Inference.Profiles;

internal static class ModelProfileApplicationContractCopy
{
    internal static IReadOnlyList<T> Snapshot<T>(IReadOnlyList<T>? values, int maximum, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        int declaredCount;
        try
        {
            declaredCount = values.Count;
        }
        catch (Exception exception)
        {
            throw new ArgumentException("The collection count is unavailable.", parameterName, exception);
        }

        if (declaredCount < 0 || declaredCount > maximum)
        {
            throw new ArgumentException("The collection exceeds the bounded contract.", parameterName);
        }

        var snapshot = new List<T>(declaredCount);
        try
        {
            foreach (var item in values)
            {
                if (snapshot.Count == maximum || item is null)
                {
                    throw new ArgumentException("The collection exceeds the bounded contract or contains null.", parameterName);
                }

                snapshot.Add(item);
            }
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ArgumentException("The collection could not be enumerated safely.", parameterName, exception);
        }

        if (snapshot.Count != declaredCount)
        {
            throw new ArgumentException("The collection's declared count does not match enumeration.", parameterName);
        }

        return Array.AsReadOnly(snapshot.ToArray());
    }
}
