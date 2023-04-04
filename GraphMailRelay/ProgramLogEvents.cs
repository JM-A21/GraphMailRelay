using MimeKit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GraphMailRelay
{
	internal static class ProgramLogEvents
	{
		// TODO: Populate event names, if necessary.


		// SmtpWorker events.
		internal static EventId SmtpWorkerInitializing = new(2000, "SmtpWorkerInitializing");
		internal static EventId SmtpWorkerValidating = new(2001, "SmtpWorkerValidating");
		internal static EventId SmtpWorkerValidated = new(2002, "SmtpWorkerValidated");
		internal static EventId SmtpWorkerValidationFailed = new(2003, "SmtpWorkerValidationFailed");
		internal static EventId SmtpWorkerCancelling = new(2004, "SmtpWorkerCancelling");
		internal static EventId SmtpWorkerStarting = new(2005, "SmtpWorkerStarting");
		internal static EventId SmtpWorkerStarted = new(2006, "SmtpWorkerStarted");
		internal static EventId SmtpWorkerStopping = new(2007, "SmtpWorkerStopping");
		internal static EventId SmtpWorkerStopped = new(2008, "SmtpWorkerStopped");

		internal static EventId SmtpWorkerUnknownError = new(2099, "SmtpWorkerUnknownError");

		internal static EventId SmtpWorkerMessageReceived = new(2100, "SmtpWorkerMessageReceived");
		internal static EventId SmtpWorkerMessageAccepted = new(2101, "SmtpWorkerMessageAccepted");
		internal static EventId SmtpWorkerMessageRejected = new(2102, "SmtpWorkerMessageRejected");
		internal static EventId SmtpWorkerMessageQueuing = new(2103, "SmtpWorkerMessageQueuing");
		internal static EventId SmtpWorkerMessageQueued = new(2104, "SmtpWorkerMessageQueued");
		
		// GraphWorker Events.
	}
}
