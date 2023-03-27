using MimeKit;
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

					// Configure the application with custom settings locations.
					.ConfigureAppConfiguration((configuration) =>
					{
						// TODO: Add path to app's ProgramData directory and the "appsettings.json" file that will be contained within.
						// TODO: Dynamic iteration through folders?
					})

					// Configure the worker services themselves and pass in needed objects.
					.ConfigureServices((hostContext, services) =>
					{

						// Build the unbounded channel object we'll use as a producer/consumer work queue
						// between SmtpWorker/RelayWorker.
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