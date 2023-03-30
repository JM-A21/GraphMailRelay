using MimeKit;
using OpenTelemetry.Trace;
using System.Threading.Channels;

namespace GraphMailRelay
{
	public class Program
	{
		private const string SmtpWorkerConfigMissingMessage = "Application failed to locate required configuration section name '" + SmtpWorkerOptions.SmtpConfiguration + "' across loaded configuration files. Application will shut down.";
		private const string GraphWorkerConfigMissingMessage = "Application failed to locate required configuration section name '" + GraphWorkerOptions.GraphConfiguration + "' across loaded configuration files. Application will shut down.";

		public static async Task Main(string[] args)
		{
			try
			{
				// Configure and build our worker services and shared objects.
				IHost host = Host.CreateDefaultBuilder(args)

					// Add the MV10 generic logger so we can log errors during configuration.
					.AddHostBuilderLogger()

					// Configure the application as a Windows Service.
					.UseWindowsService((options) =>
					{
						options.ServiceName = "GraphMailRelayService";
					})

					// Configure the application with custom settings locations.
					.ConfigureAppConfiguration((configuration) =>
					{
						// TODO: Add path to app's ProgramData directory and the "appsettings.json" file that will be contained within.
						// TODO: Dynamic iteration through folders?
#if DEBUG
						// TODO: Make this dynamic. Ideally both application and WiX should be dynamicall fed from properties somewhere.
						// This must match what's configured in WiX, otherwise the file path will be wrong and the settings won't load.
						configuration.AddJsonFile(
							Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "JM-A21", "GraphMailRelay", "appsettings.json"),
							optional: true,
							reloadOnChange: false);
#else
						// TODO: Make this dynamic. Ideally both application and WiX should be dynamicall fed from properties somewhere.
						// This must match what's configured in WiX, otherwise the file path will be wrong and the settings won't load.
						configuration.AddJsonFile(
							Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "JM-A21", "GraphMailRelay", "appsettings.json"),
							optional: false,
							reloadOnChange: false);
#endif
					})

					// Configure the worker services themselves and pass in needed objects.
					.ConfigureServices((hostContext, services) =>
					{
						// Build the unbounded channel object we'll use as a producer/consumer work queue between SmtpWorker/GraphWorker.
						var channelMailQueue = Channel.CreateUnbounded<KeyValuePair<Guid, MimeMessage>>(
							new UnboundedChannelOptions
							{
								SingleWriter = false,
								SingleReader = true
							});

						// Add the queue as a singleton. Hope this is right! :) 
						services.AddSingleton(channelMailQueue);

						// Attempt to retrieve the SmtpWorker settings. GetRequiredSection throws
						// InvalidOperationException if section is missing. Then confirm the results
						// are not null.
						var optionsSmtp = hostContext.Configuration.GetRequiredSection(SmtpWorkerOptions.SmtpConfiguration).Get<SmtpWorkerOptions>();
						if (optionsSmtp is not null)
						{
							services.AddSingleton(optionsSmtp);
						}
						else
						{
							throw new NullReferenceException(SmtpWorkerConfigMissingMessage);
						}

						// Build and add the SmtpWorker to the hosted services.
						services.AddHostedService<SmtpWorker>();

						// Attempt to retrieve the GraphWorker settings. GetRequiredSection throws
						// InvalidOperationException if section is missing. Then confirm the results
						// are not null.
						var optionsGraph = hostContext.Configuration.GetRequiredSection(GraphWorkerOptions.GraphConfiguration).Get<GraphWorkerOptions>();
						if (optionsGraph is not null)
						{
							services.AddSingleton(optionsGraph);
						}
						else
						{
							throw new NullReferenceException(GraphWorkerConfigMissingMessage);
						}

						// Build and add the SmtpWorker to the hosted services.
						services.AddHostedService<GraphWorker>();

						// Configure OpenTelemetry and web request logging.
						services.AddOpenTelemetry()
							.WithTracing((builder) =>
							{
								builder
									.AddSource(nameof(GraphWorker))
									.AddHttpClientInstrumentation(httpOptions =>
									{
										httpOptions.FilterHttpRequestMessage = (httpRequestMessage) =>
										{
											return optionsGraph.HttpResponseCapture.GetValueOrDefault();
										};

										httpOptions.EnrichWithHttpResponseMessage = (activity, httpResponseMessage) =>
										{
											if (httpResponseMessage is not null)
											{
												activity.SetTag("http.response_content", httpResponseMessage.Content.ReadAsStringAsync().Result);
											}
										};
									})
									.AddConsoleExporter();
							});
					})

					.Build();

				// Kick off host application startup.
				host.Run();
			}
			catch (Exception ex)
			{
				HostBuilderLogger.Logger.LogError("Application caught exception during service host startup", ex);
				await HostBuilderLogger.Logger.TerminalEmitCachedMessages(args);
				return;
			}
		}
	}
}