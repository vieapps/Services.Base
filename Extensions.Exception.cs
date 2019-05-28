#region Related components
using System;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.Dynamic;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using WampSharp.V2.Core.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Microsoft.Extensions.Logging;
using net.vieapps.Components.Utility;
using net.vieapps.Components.Repository;
#endregion

namespace net.vieapps.Services
{
	/// <summary>
	/// Extension methods for working with services in the VIEApps NGX
	/// </summary>
	public static partial class Extensions
	{
		/// <summary>
		/// Gets the stack trace of this error exception
		/// </summary>
		/// <param name="exception"></param>
		/// <returns></returns>
		public static string GetStack(this Exception exception)
		{
			var stack = "";
			if (exception != null && exception is WampException)
			{
				var details = (exception as WampException).GetDetails();
				stack = details.Item4?.Replace("\\r", "\r")?.Replace("\\n", "\n")?.Replace(@"\\", @"\");
				if (details.Item6 != null)
					stack = details.Item6.ToString(Formatting.Indented).Replace("\\r", "\r").Replace("\\n", "\n").Replace(@"\\", @"\");
			}
			else if (exception != null)
			{
				stack = exception.StackTrace;
				var inner = exception.InnerException;
				var counter = 0;
				while (inner != null)
				{
					counter++;
					stack += "\r\n" + $"--- Inner ({counter}): ---------------------- " + "\r\n"
						+ "> Message: " + inner.Message + "\r\n"
						+ "> Type: " + inner.GetType().ToString() + "\r\n"
						+ inner.StackTrace;
					inner = inner.InnerException;
				}
			}
			return stack;
		}

		/// <summary>
		/// Gets the details of a WAMP exception
		/// </summary>
		/// <param name="exception"></param>
		/// <param name="requestInfo"></param>
		/// <returns></returns>
		public static Tuple<int, string, string, string, Exception, JObject> GetDetails(this WampException exception, RequestInfo requestInfo = null)
		{
			var code = (int)HttpStatusCode.InternalServerError;
			var message = "";
			var type = "";
			var stack = "";
			Exception inner = null;
			JObject jsonException = null;

			// unavailable
			if (exception.ErrorUri.Equals("wamp.error.no_such_procedure") || exception.ErrorUri.Equals("wamp.error.callee_unregistered"))
			{
				if (exception.Arguments != null && exception.Arguments.Length > 0 && exception.Arguments[0] != null && exception.Arguments[0] is JValue)
				{
					message = (exception.Arguments[0] as JValue).Value.ToString();
					var start = message.IndexOf("'");
					var end = message.IndexOf("'", start + 1);
					message = $"The service ({message.Substring(start + 1, end - start - 1).Replace("'", "")}) is unavailable";
				}
				else
					message = "The service is unavailable";

				type = "ServiceUnavailableException";
				stack = exception.StackTrace;
			}

			// cannot serialize
			else if (exception.ErrorUri.Equals("wamp.error.invalid_argument"))
			{
				message = "Cannot serialize or deserialize one of arguments, all arguments must be instance of a serializable class - interfaces are not be deserialized";
				if (exception.Arguments != null && exception.Arguments.Length > 0 && exception.Arguments[0] != null && exception.Arguments[0] is JValue)
					message += $" => {(exception.Arguments[0] as JValue).Value}";
				type = "SerializationException";
				stack = exception.StackTrace;
			}

			// runtime error
			else if (exception.ErrorUri.Equals("wamp.error.runtime_error"))
			{
				inner = exception;

				if (exception.Arguments != null && exception.Arguments.Length > 0 && exception.Arguments[0] != null && exception.Arguments[0] is JObject)
					foreach (var info in exception.Arguments[0] as JObject)
					{
						if (info.Value != null && info.Value is JValue && (info.Value as JValue).Value != null)
							stack += (stack.Equals("") ? "" : "\r\n" + $"----- Inner ({info.Key}) --------------------" + "\r\n")
								+ (info.Value as JValue).Value.ToString();
					}

				if (requestInfo == null && exception.Arguments != null && exception.Arguments.Length > 2 && exception.Arguments[2] != null && exception.Arguments[2] is JObject)
				{
					var info = (exception.Arguments[2] as JObject).First;
					if (info != null && info is JProperty && (info as JProperty).Name.Equals("RequestInfo") && (info as JProperty).Value != null && (info as JProperty).Value is JObject)
						requestInfo = ((info as JProperty).Value as JToken).FromJson<RequestInfo>();
				}

				jsonException = exception.Arguments != null && exception.Arguments.Length > 4 && exception.Arguments[4] != null && exception.Arguments[4] is JObject
					? (exception.Arguments[4] as JObject).GetJsonException()
					: null;

				message = jsonException != null
					? (jsonException["Message"] as JValue).Value.ToString()
					: $"Error occurred at \"services.{(requestInfo?.ServiceName ?? "unknown").ToLower()}\"";

				type = jsonException != null
					? (jsonException["Type"] as JValue).Value.ToString().ToArray('.').Last()
					: "ServiceOperationException";
			}

			// unknown
			else
			{
				message = exception.Message;
				type = exception.GetTypeName(true);
				stack = exception.StackTrace;
				inner = exception.InnerException;
			}

			// status code
			switch (type)
			{
				case "FileNotFoundException":
				case "ServiceNotFoundException":
				case "InformationNotFoundException":
					code = (int)HttpStatusCode.NotFound;
					break;

				case "MethodNotAllowedException":
					code = (int)HttpStatusCode.MethodNotAllowed;
					break;

				case "NotImplementedException":
					code = (int)HttpStatusCode.NotImplemented;
					break;

				case "AccessDeniedException":
					code = (int)HttpStatusCode.Forbidden;
					break;

				case "UnauthorizedException":
					code = (int)HttpStatusCode.Unauthorized;
					break;

				default:
					if (type.Contains("Invalid"))
						code = (int)HttpStatusCode.BadRequest;
					else if (type.Contains("Unavailable"))
						code = (int)HttpStatusCode.ServiceUnavailable;
					break;
			}

			return new Tuple<int, string, string, string, Exception, JObject>(code, message, type, stack, inner, jsonException);
		}

		static JObject GetJsonException(this JToken exception)
		{
			var json = new JObject
			{
				{ "Message", exception["Message"] },
				{ "Type", exception["ClassName"] },
				{ "Method", exception["ExceptionMethod"] },
				{ "Source", exception["Source"] },
				{ "Stack", exception["StackTraceString"] },
			};

			var inner = exception["InnerException"];
			if (inner != null && inner is JObject)
				json.Add(new JProperty("InnerException", inner.GetJsonException()));

			return json;
		}
	}
}