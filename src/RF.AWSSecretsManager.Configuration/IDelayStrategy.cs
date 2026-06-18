namespace RF.AWSSecretsManager.Configuration;

/// <summary>
/// Abstraction over time delays to make retry logic testable.
/// </summary>
public interface IDelayStrategy
{
    /// <summary>
    /// Performs a delay for the specified duration.
    /// </summary>
    /// <param name="milliseconds">The delay duration in milliseconds.</param>
    void Delay(int milliseconds);
}

/// <summary>
/// Default <see cref="IDelayStrategy"/> implementation that uses <see cref="System.Threading.Thread.Sleep(int)"/>.
/// </summary>
public sealed class ThreadSleepDelayStrategy : IDelayStrategy
{
    /// <inheritdoc />
    public void Delay(int milliseconds)
    {
        if (milliseconds <= 0)
        {
            return;
        }

        Thread.Sleep(milliseconds);
    }
}

