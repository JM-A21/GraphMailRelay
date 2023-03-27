namespace GraphMailRelay
{
	internal class SmtpWorkerOptions
	{
		public const string SmtpConfiguration = "SmtpConfiguration";

		public string? ServerName { get; set; }
		public int? ServerPort { get; set; }
		public List<string>? AllowedSenderAddresses { get; set; }
	}
}
