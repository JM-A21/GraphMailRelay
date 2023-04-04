namespace GraphMailRelay
{
	internal static class RelayLogEvents
	{
		// Generic events (1xxx)

		// SmtpWorker events (2xxx)

		internal static EventId SmtpWorkerInitializing = new(2000);
		internal static EventId SmtpWorkerValidating = new(2001);
		internal static EventId SmtpWorkerValidated = new(2002);
		internal static EventId SmtpWorkerValidationFailed = new(2003);
		internal static EventId SmtpWorkerValidationFailedUnhandled = new(2004);
		internal static EventId SmtpWorkerCancelling = new(2005);
		internal static EventId SmtpWorkerStarting = new(2006);
		internal static EventId SmtpWorkerStarted = new(2007);
		internal static EventId SmtpWorkerStopping = new(2008);
		internal static EventId SmtpWorkerStopped = new(2009);

		internal static EventId SmtpWorkerMessageReceived = new(2100);
		internal static EventId SmtpWorkerMessageAccepted = new(2101);
		internal static EventId SmtpWorkerMessageRejected = new(2102);
		internal static EventId SmtpWorkerMessageSaving = new(2103);
		internal static EventId SmtpWorkerMessageQueuing = new(2104);
		internal static EventId SmtpWorkerMessageQueued = new(2105);

		internal static EventId SmtpWorkerUnknownError = new(2999);

		// GraphWorker events (3xxx)

		internal static EventId GraphWorkerInitializing = new(3000);
		internal static EventId GraphWorkerValidating = new(3001);
		internal static EventId GraphWorkerValidated = new(3002);
		internal static EventId GraphWorkerValidationFailed = new(3003);
		internal static EventId GraphWorkerValidationFailedUnhandled = new(3004);
		internal static EventId GraphWorkerCancelling = new(3005);
		internal static EventId GraphWorkerStarting = new(3006);
		internal static EventId GraphWorkerStarted = new(3007);
		internal static EventId GraphWorkerStopping = new(3008);
		internal static EventId GraphWorkerStopped = new(3009);

		internal static EventId GraphWorkerMessageDequeued = new(3100);
		internal static EventId GraphWorkerRequestBuildStarted = new(3101);
		internal static EventId GraphWorkerRequestBuildFinished = new(3102);
		internal static EventId GraphWorkerRequestSending = new(3103);
		internal static EventId GraphWorkerRequestComplete = new(3104);

		internal static EventId GraphWorkerRequestCanceled = new(3900);
		internal static EventId GraphWorkerRequestFaulted = new(3901);
		internal static EventId GraphWorkerUnknownError = new(3999);
	}
}
