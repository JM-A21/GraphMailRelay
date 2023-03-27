namespace GraphMailRelay
{
	internal class SmtpWorkerOptions
	{
		public const string RelayConfiguration = "SmtpConfiguration";

		public string? ServerName { get; set; }
		public int? ServerPort { get; set; }
		public List<string>? AllowedSenderAddresses { get; set; }
	}
}
