namespace SmtpRelay.Relay;

/// <summary>Delivery error towards the smart host, permanent or transient.</summary>
public sealed class UpstreamDeliveryException : Exception
{
    public bool IsPermanent { get; }

    public UpstreamDeliveryException(string message, bool isPermanent, Exception? inner = null)
        : base(message, inner)
    {
        IsPermanent = isPermanent;
    }
}
