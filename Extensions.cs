#region Related components
using System;
using System.IO;
using System.Net;
using System.Web;

using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
#endregion

namespace net.vieapps.Services
{
	/// <summary>
	/// Extension methods for working with services
	/// </summary>
	public static class Extensions
	{
		/// <summary>
		/// Gets the approriate HTTP Status Code of the exception
		/// </summary>
		/// <param name="exception"></param>
		/// <returns></returns>
		public static int GetHttpStatusCode(this Exception exception)
		{
			if (exception is FileNotFoundException || exception is ServiceNotFoundException || exception is InformationNotFoundException)
				return (int)HttpStatusCode.NotFound;

			if (exception is AccessDeniedException)
				return (int)HttpStatusCode.Forbidden;

			if (exception is UnauthorizedException)
				return (int)HttpStatusCode.Unauthorized;

			if (exception is MethodNotAllowedException)
				return (int)HttpStatusCode.MethodNotAllowed;

			if (exception is InvalidRequestException)
				return (int)HttpStatusCode.BadRequest;

			if (exception is NotImplementedException)
				return (int)HttpStatusCode.NotImplemented;

			if (exception is ConnectionTimeoutException)
				return (int)HttpStatusCode.RequestTimeout;

			return (int)HttpStatusCode.InternalServerError;
		}

		/// <summary>
		/// Show HTTP error
		/// </summary>
		/// <param name="context"></param>
		/// <param name="code"></param>
		/// <param name="message"></param>
		/// <param name="type"></param>
		/// <param name="correlationID"></param>
		/// <param name="stack"></param>
		/// <param name="showStack"></param>
		public static void ShowHttpError(this HttpContext context, int code, string message, string type, string correlationID = null, string stack = null, bool showStack = true)
		{
			code = code < 1 ? (int)HttpStatusCode.InternalServerError : code;

			context.Response.TrySkipIisCustomErrors = true;
			context.Response.StatusCode = code;
			context.Response.Cache.SetNoStore();
			context.Response.ContentType = "text/html";

			context.Response.ClearContent();
			context.Response.Output.Write("<!DOCTYPE html>\r\n");
			context.Response.Output.Write("<html xmlns=\"http://www.w3.org/1999/xhtml\">\r\n");
			context.Response.Output.Write("<head><title>Error " + code.ToString() + "</title></head>\r\n<body>\r\n");
			context.Response.Output.Write("<h1>HTTP " + code.ToString() + " - " + message.Replace("<", "&lt;").Replace(">", "&gt;") + "</h1>\r\n");
			context.Response.Output.Write("<hr/>\r\n");
			context.Response.Output.Write("<div>Type: " + type + (!string.IsNullOrWhiteSpace(correlationID) ? " - Correlation ID: " + correlationID : "") + "</div>\r\n");
			if (!string.IsNullOrWhiteSpace(stack) && showStack)
				context.Response.Output.Write("<div><br/>Stack:</div>\r\n<blockquote>" + stack.Replace("<", "&lt;").Replace(">", "&gt;").Replace("\n", "<br/>").Replace("\r", "").Replace("\t", "") + "</blockquote>\r\n");
			context.Response.Output.Write("</body>\r\n</html>");

			if (message.IsContains("potentially dangerous"))
				context.Response.End();
		}
	}
}