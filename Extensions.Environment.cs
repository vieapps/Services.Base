#region Related components
using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Services
{
	/// <summary>
	/// Extension methods for working with services in the VIEApps NGX
	/// </summary>
	public static partial class Extensions
	{

		#region Get information of os/platform/environment
		/// <summary>
		/// Gets the name of the runtime OS platform
		/// </summary>
		/// <returns></returns>
		public static string GetRuntimeOS()
			=> RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
				? "macOS"
				: RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
					? "Linux"
					: "Windows";

		/// <summary>
		/// Gets the information of the runtime platform
		/// </summary>
		/// <returns></returns>
		public static string GetRuntimePlatform(bool getFrameworkDescription = true)
			=> (getFrameworkDescription ? $"{RuntimeInformation.FrameworkDescription.Trim()} @ " : "")
				+ $"{Extensions.GetRuntimeOS()} {RuntimeInformation.OSArchitecture.ToString().ToLower()} "
				+ $"({(RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "Macintosh; Intel Mac OS X; " : "")}{RuntimeInformation.OSDescription.Trim()})";

		/// <summary>
		/// Gets the runtime environment information
		/// </summary>
		/// <param name="seperator"></param>
		/// <returns></returns>
		public static string GetRuntimeEnvironment(string seperator = "\r\n\t")
			=> $"- User: {Environment.UserName.ToLower()} @ {Environment.MachineName.ToLower()}{seperator ?? "\r\n\t"}- Platform: {Extensions.GetRuntimePlatform()}";
		#endregion

		#region Get unique name & end-point
		/// <summary>
		/// Gets the unique name of a business service
		/// </summary>
		/// <param name="name">The string that presents name of a business service</param>
		/// <param name="user">The string that presents name of the user who runs the process of the business service</param>
		/// <param name="host">The string that presents name of the host that runs the process of the business service</param>
		/// <param name="platform">The string that presents name of the platform that runs the process of the business service</param>
		/// <param name="os">The string that presents name of the operating system that runs the process of the business service</param>
		/// <returns>The string that presents unique name of a business service at a host</returns>
		public static string GetUniqueName(string name, string user = null, string host = null, string platform = null, string os = null)
			=> $"{(name ?? "unknown").Trim().ToLower()}.{(user ?? Environment.UserName).Trim().ToLower()}-{(host ?? Environment.MachineName).Trim().ToLower()}-" + $"{(platform ?? RuntimeInformation.FrameworkDescription).Trim()} @ {os ?? Extensions.GetRuntimeOS()}".GenerateUUID();

		/// <summary>
		/// Gets the unique name of a business service
		/// </summary>
		/// <param name="name">The string that presents name of a business service</param>
		/// <param name="args">The starting arguments</param>
		/// <returns>The string that presents unique name of a business service at a host</returns>
		public static string GetUniqueName(string name, string[] args)
		{
			var user = args?.FirstOrDefault(a => a.IsStartsWith("/run-user:"));
			var host = args?.FirstOrDefault(a => a.IsStartsWith("/run-host:"));
			var platform = args?.FirstOrDefault(a => a.IsStartsWith("/run-platform:"));
			var os = args?.FirstOrDefault(a => a.IsStartsWith("/run-os:"));
			return Extensions.GetUniqueName(name, user?.Replace(StringComparison.OrdinalIgnoreCase, "/run-user:", "").Trim().UrlDecode(), host?.Replace(StringComparison.OrdinalIgnoreCase, "/run-host:", "").Trim().UrlDecode(), platform?.Replace(StringComparison.OrdinalIgnoreCase, "/run-platform:", "").Trim().UrlDecode(), os?.Replace(StringComparison.OrdinalIgnoreCase, "/run-os:", "").Trim().UrlDecode());
		}

		/// <summary>
		/// Gets the resolved URI with IP address and port
		/// </summary>
		/// <param name="uri"></param>
		/// <returns></returns>
		public static string GetResolvedURI(this Uri uri)
		{
			var host = "";
			if (!IPAddress.TryParse(uri.Host, out var address))
			{
				address = Dns.GetHostAddresses(uri.Host).FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork || ip.AddressFamily == AddressFamily.InterNetworkV6);
				host = address == null
					? $" => Could not resolve host \"{host}\""
					: $" => {uri.Scheme}://{new IPEndPoint(address, uri.Port)}{uri.PathAndQuery}";
			}
			return $"{uri}{host}";
		}
		#endregion

		#region Send service info to API Gateway
		/// <summary>
		/// Sends the service information to API Gateway
		/// </summary>
		/// <param name="rtuService"></param>
		/// <param name="serviceName">The service name</param>
		/// <param name="args">The services' arguments (for prepare the unique name)</param>
		/// <param name="running">The running state</param>
		/// <param name="available">The available state</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static Task SendServiceInfoAsync(this IRTUService rtuService, string serviceName, string[] args, bool running, bool available = true, CancellationToken cancellationToken = default)
		{
			var arguments = (args ?? new string[] { }).Where(arg => !arg.IsStartsWith("/controller-id:")).ToArray();
			var invokeInfo = arguments.FirstOrDefault(arg => arg.IsStartsWith("/call-user:")) ?? "";

			if (!string.IsNullOrWhiteSpace(invokeInfo))
			{
				invokeInfo = invokeInfo.Replace(StringComparison.OrdinalIgnoreCase, "/call-user:", "").UrlDecode();
				var host = arguments.FirstOrDefault(arg => arg.IsStartsWith("/call-host:"));
				var platform = arguments.FirstOrDefault(arg => arg.IsStartsWith("/call-platform:"));
				var os = arguments.FirstOrDefault(arg => arg.IsStartsWith("/call-os:"));
				if (!string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(platform) && !string.IsNullOrWhiteSpace(os))
					invokeInfo += $" [Host: {host.Replace(StringComparison.OrdinalIgnoreCase, "/call-host:", "").UrlDecode()} - Platform: {platform.Replace(StringComparison.OrdinalIgnoreCase, "/call-platform:", "").UrlDecode()} @ {os.Replace(StringComparison.OrdinalIgnoreCase, "/call-os:", "").UrlDecode()}]";
			}
			else
				invokeInfo = $"{Environment.UserName.ToLower()} [Host: {Environment.MachineName.ToLower()} - Platform: {Extensions.GetRuntimePlatform()}]";

			return rtuService == null || string.IsNullOrWhiteSpace(serviceName)
				? Task.CompletedTask
				: rtuService.SendInterCommunicateMessageAsync(new CommunicateMessage("APIGateway")
				{
					Type = "Service#Info",
					Data = new ServiceInfo
					{
						Name = serviceName.ToLower(),
						UniqueName = Extensions.GetUniqueName(serviceName, arguments),
						ControllerID = args?.FirstOrDefault(arg => arg.IsStartsWith("/controller-id:"))?.Replace("/controller-id:", "") ?? "Unknown",
						InvokeInfo = invokeInfo,
						Available = available,
						Running = running
					}.ToJson()
				}, cancellationToken);
		}
		#endregion

	}
}