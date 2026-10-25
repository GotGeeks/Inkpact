namespace Inkpact.Data.Upgrade;

public class BackupFailedException(string message, Exception? inner = null) : Exception(message, inner);
