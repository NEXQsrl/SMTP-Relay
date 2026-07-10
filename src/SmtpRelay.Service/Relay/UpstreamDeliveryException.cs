namespace SmtpRelay.Relay;

/// <summary>Errore di consegna verso l'upstream, con distinzione permanente/transitorio.</summary>
public sealed class UpstreamDeliveryException : Exception
{
    public bool IsPermanent { get; }

    public UpstreamDeliveryException(string message, bool isPermanent, Exception? inner = null)
        : base(message, inner)
    {
        IsPermanent = isPermanent;
    }
}
