﻿namespace GraphMailRelay
{
	internal class GraphWorkerOptions
	{
		public const string GraphConfiguration = "GraphConfiguration";

		public string? AzureTenantId { get; set; }
		public string? AzureClientId { get; set; }
		public string? AzureClientSecret { get; set; }
		public string? AzureMailUser { get; set; }
		public string? EnvironmentName { get; set; }
		public string? FromAddressOverride { get; set; }
		public bool? HttpResponseCapture { get; set; }
	}
}
