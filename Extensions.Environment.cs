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

		/// <summary>
		/// Gets the run-time arguments (for working with service/node identity)
		/// </summary>
		/// <returns></returns>
		public static Tuple<string, string, string, string> GetRuntimeArguments()
			=> new Tuple<string, string, string, string>
			(
				Environment.UserName.Trim().ToLower(),
				Environment.MachineName.Trim().ToLower(),
				RuntimeInformation.FrameworkDescription.Trim(),
				Extensions.GetRuntimePlatform(false)
			);
		#endregion

		#region Get node identity, unique name & end-point
		/// <summary>
		/// Gets the identity of a node that running a service
		/// </summary>
		/// <param name="user">The user on the host that running the service</param>
		/// <param name="host">The host that running the service</param>
		/// <param name="platform">The information (description) of the running platform (framework)</param>
		/// <param name="os">The information of the operating system</param>
		/// <returns>The string that presents the identity of a node (include user and host)</returns>
		public static string GetNodeID(string user = null, string host = null, string platform = null, string os = null)
		{
			var runtimeArguments = Extensions.GetRuntimeArguments();
			user = user?.Trim().ToLower() ?? runtimeArguments.Item1;
			host = host?.Trim().ToLower() ?? runtimeArguments.Item2;
			platform = platform?.Trim() ?? runtimeArguments.Item3;
			os = os?.Trim() ?? runtimeArguments.Item4;
			return $"{user}-{host}-" + $"{platform} @ {os}".GenerateUUID();
		}

		/// <summary>
		/// Gets the identity of a node that running a service
		/// </summary>
		/// <param name="args">The running (starting) arguments</param>
		/// <returns>The string that presents the identity of a node (include user and host)</returns>
		public static string GetNodeID(IEnumerable<string> args)
			=> Extensions.GetNodeID
			(
				args?.FirstOrDefault(arg => arg.IsStartsWith("/run-user:"))?.Replace(StringComparison.OrdinalIgnoreCase, "/run-user:", "").UrlDecode(),
				args?.FirstOrDefault(arg => arg.IsStartsWith("/run-host:"))?.Replace(StringComparison.OrdinalIgnoreCase, "/run-host:", "").UrlDecode(),
				args?.FirstOrDefault(arg => arg.IsStartsWith("/run-platform:"))?.Replace(StringComparison.OrdinalIgnoreCase, "/run-platform:", "").UrlDecode(),
				args?.FirstOrDefault(arg => arg.IsStartsWith("/run-os:"))?.Replace(StringComparison.OrdinalIgnoreCase, "/run-os:", "").UrlDecode()
			);

		/// <summary>
		/// Gets the unique name of a business service
		/// </summary>
		/// <param name="name">The string that presents the name of a service</param>
		/// <param name="user">The user on the host that running the service</param>
		/// <param name="host">The host that running the service</param>
		/// <param name="platform">The information (description) of the running platform (framework)</param>
		/// <param name="os">The information of the operating system</param>
		/// <returns>The string that presents unique name of a business service at a host</returns>
		public static string GetUniqueName(string name, string user = null, string host = null, string platform = null, string os = null)
			=> $"{(name ?? "unknown").Trim().ToLower()}.{Extensions.GetNodeID(user, host, platform, os)}";

		/// <summary>
		/// Gets the unique name of a business service
		/// </summary>
		/// <param name="name">The string that presents the name of a service</param>
		/// <param name="args">The running (starting) arguments</param>
		/// <returns>The string that presents unique name of a service</returns>
		public static string GetUniqueName(string name, IEnumerable<string> args)
			=> $"{(name ?? "unknown").Trim().ToLower()}.{Extensions.GetNodeID(args)}";

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
		/// Gets the invoke information
		/// </summary>
		/// <returns></returns>
		public static string GetInvokeInfo()
		{
			var runtimeArguments = Extensions.GetRuntimeArguments();
			return $"{runtimeArguments.Item1} [Host: {runtimeArguments.Item2} - Platform: {runtimeArguments.Item3} @ {runtimeArguments.Item4}]";
		}

		/// <summary>
		/// Sends the service information to API Gateway
		/// </summary>
		/// <param name="serviceName">The service name</param>
		/// <param name="args">The services' arguments (for prepare the unique name)</param>
		/// <param name="running">The running state</param>
		/// <param name="available">The available state</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static Task SendServiceInfoAsync(string serviceName, IEnumerable<string> args, bool running, bool available = true, CancellationToken cancellationToken = default)
			=> string.IsNullOrWhiteSpace(serviceName)
				? Task.CompletedTask
				: new CommunicateMessage("APIGateway")
				{
					Type = "Service#Info",
					Data = new ServiceInfo
					{
						Name = serviceName.ToLower(),
						UniqueName = Extensions.GetUniqueName(serviceName, args),
						ControllerID = args?.FirstOrDefault(arg => arg.IsStartsWith("/controller-id:"))?.Replace("/controller-id:", "") ?? "Unknown",
						InvokeInfo = Extensions.GetInvokeInfo(),
						Available = available,
						Running = running
					}.ToJson()
				}.SendAsync(cancellationToken);
		#endregion

	}
}