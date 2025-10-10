#region Related components
using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using WampSharp.V2;
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
			=> RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
				? "Windows"
				: RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
					? "macOS"
					: RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
						? "Linux"
#if NETSTANDARD2_0
						: "Generic OS";
#else
						: RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD) ? "FreeBSD" : "Generic OS";
#endif

		/// <summary>
		/// Gets the information of the runtime platform
		/// </summary>
		/// <returns></returns>
		public static string GetRuntimePlatform(bool getFrameworkDescription = true)
			=> (getFrameworkDescription ? $"{RuntimeInformation.FrameworkDescription.Trim()} @ " : "")
				+ $"{Extensions.GetRuntimeOS()} {RuntimeInformation.OSArchitecture.ToString().ToLower()} "
				+ $"({(RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "Macintosh; Mac OS X; " : "")}{RuntimeInformation.OSDescription.Trim()})";

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
		public static (string User, string Host, string Platform, string OS) GetRuntimeArguments()
			=> (Environment.UserName.Trim().ToLower(), Environment.MachineName.Trim().ToLower(), RuntimeInformation.FrameworkDescription.Trim(), Extensions.GetRuntimePlatform(false));

		/// <summary>
		/// Gets the name of the app's OS
		/// </summary>
		/// <param name="userAgent"></param>
		/// <returns></returns>
		public static string GetOSInfo(string userAgent)
			=> userAgent.IsContains("iPhone") || userAgent.IsContains("iPad") || userAgent.IsContains("iPod") || userAgent.IsContains("iOS")
				? "iOS"
				: userAgent.IsContains("Android")
					? "Android"
					: userAgent.IsContains("Windows Phone")
						? "Windows Phone"
						: userAgent.IsContains("BlackBerry") || userAgent.IsContains("BB10") || userAgent.IsContains("RIM Tablet OS")
							? "BlackBerry" + (userAgent.IsContains("BB10") ? "10" : "OS")
							: userAgent.IsContains("IEMobile") || userAgent.IsContains("Opera Mini") || userAgent.IsContains("MDP/")
								? "Mobile OS"
								: userAgent.IsContains("Windows")
									? "Windows"
									: userAgent.IsContains("Mac OS")
										? "macOS"
										: userAgent.IsContains("Linux")
											? "Linux"
											: "Generic OS";
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
			var (User, Host, Platform, OS) = Extensions.GetRuntimeArguments();
			return $"{user?.Trim().ToLower() ?? User}-{host?.Trim().ToLower() ?? Host}-" + $"{platform?.Trim() ?? Platform} @ {os?.Trim() ?? OS}".GenerateUUID();
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
		/// <param name="node">The string that presents the identity of a node</param>
		/// <returns>The string that presents unique name of a business service at a host</returns>
		public static string GetUniqueName(string name, string node = null)
			=> $"{(name ?? "unknown").Trim().ToLower()}.{(string.IsNullOrWhiteSpace(node) ? Extensions.GetNodeID() : node)}";

		/// <summary>
		/// Gets the unique name of a business service
		/// </summary>
		/// <param name="name">The string that presents the name of a service</param>
		/// <param name="args">The running (starting) arguments</param>
		/// <returns>The string that presents unique name of a service</returns>
		public static string GetUniqueName(string name, IEnumerable<string> args)
			=> Extensions.GetUniqueName(name, Extensions.GetNodeID(args));

		/// <summary>
		/// Gets the unique name of a business service
		/// </summary>
		/// <param name="name">The string that presents the name of a service</param>
		/// <param name="user">The user on the host that running the service</param>
		/// <param name="host">The host that running the service</param>
		/// <param name="platform">The information (description) of the running platform (framework)</param>
		/// <param name="os">The information of the operating system</param>
		/// <returns>The string that presents unique name of a business service at a host</returns>
		public static string GetUniqueName(string name, string user, string host, string platform, string os)
			=> Extensions.GetUniqueName(name, Extensions.GetNodeID(user, host, platform, os));

		/// <summary>
		/// Gets the resolved URI with IP address and port
		/// </summary>
		/// <param name="uri"></param>
		/// <returns></returns>
		public static string GetResolvedURI(this Uri uri)
		{
			var host = "";
			if (!IPAddress.TryParse(uri.Host, out var address))
				try
				{
					address = Dns.GetHostAddresses(uri.Host).FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork || ip.AddressFamily == AddressFamily.InterNetworkV6);
					host = address == null
						? $" => Could not resolve host \"{host}\""
						: $" => {uri.Scheme}://{new IPEndPoint(address, uri.Port)}{uri.PathAndQuery}";
				}
				catch { }
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
			var (User, Host, Platform, OS) = Extensions.GetRuntimeArguments();
			return $"{User} [Host: {Host} - Platform: {Platform} @ {OS}]";
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
		{
			if (!string.IsNullOrWhiteSpace(serviceName))
				new CommunicateMessage("APIGateway")
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
				}.Send();
			return Task.CompletedTask;
		}
		#endregion

		#region ReactiveX extensions
		/// <summary>
		/// Subscribes an element handler to an observable sequence.
		/// </summary>
		/// <typeparam name="T">The type of the elements in the source sequence.</typeparam>
		/// <param name="source">Observable sequence to subscribe to.</param>
		/// <param name="onNext">Action to invoke for each element in the observable sequence.</param>
		/// <param name="delay">The times (miliseconds) for delaying.</param>
		/// <param name="throttle">The times (miliseconds) for throttling.</param>
		/// <returns><see cref="IDisposable"/> object used to unsubscribe from the observable sequence.</returns>
		/// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="onNext"/> is <c>null</c>.</exception>
		public static IDisposable Subscribe<T>(this IObservable<T> source, Action<T> onNext, int delay, int throttle = 0)
			=> (delay > 0 && throttle > 0
				? source.Delay(TimeSpan.FromMilliseconds(delay)).Throttle(TimeSpan.FromMilliseconds(throttle))
				: delay > 0
					? source.Delay(TimeSpan.FromMilliseconds(delay))
					: throttle > 0
						? source.Throttle(TimeSpan.FromMilliseconds(throttle))
						: source
			).Subscribe(onNext);

		/// <summary>
		/// Subscribes an element handler, and an exception handler to an observable sequence.
		/// </summary>
		/// <typeparam name="T">The type of the elements in the source sequence.</typeparam>
		/// <param name="source">Observable sequence to subscribe to.</param>
		/// <param name="onNext">Action to invoke for each element in the observable sequence.</param>
		/// <param name="onError">Action to invoke upon exceptional termination of the observable sequence.</param>
		/// <param name="delay">The times (miliseconds) for delaying.</param>
		/// <param name="throttle">The times (miliseconds) for throttling.</param>
		/// <returns><see cref="IDisposable"/> object used to unsubscribe from the observable sequence.</returns>
		/// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="onNext"/> or <paramref name="onError"/> is <c>null</c>.</exception>
		public static IDisposable Subscribe<T>(this IObservable<T> source, Action<T> onNext, Action<Exception> onError, int delay, int throttle = 0)
			=> (delay > 0 && throttle > 0
				? source.Delay(TimeSpan.FromMilliseconds(delay)).Throttle(TimeSpan.FromMilliseconds(throttle))
				: delay > 0
					? source.Delay(TimeSpan.FromMilliseconds(delay))
					: throttle > 0
						? source.Throttle(TimeSpan.FromMilliseconds(throttle))
						: source
			).Subscribe(onNext, onError);

		/// <summary>
		/// Subscribes an element handler to an observable sequence.
		/// </summary>
		/// <typeparam name="T">The type of the elements in the source sequence.</typeparam>
		/// <param name="source">Observable sequence to subscribe to.</param>
		/// <param name="onNext">Action to invoke for each element in the observable sequence.</param>
		/// <param name="delay">The times (miliseconds) for delaying.</param>
		/// <param name="throttle">The times (miliseconds) for throttling.</param>
		/// <returns><see cref="IDisposable"/> object used to unsubscribe from the observable sequence.</returns>
		/// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="onNext"/> is <c>null</c>.</exception>
		public static IDisposable Subscribe<T>(this IObservable<T> source, Func<T, Task> onNext, int delay = 0, int throttle = 0)
			=> (delay > 0 && throttle > 0
				? source.Delay(TimeSpan.FromMilliseconds(delay)).Throttle(TimeSpan.FromMilliseconds(throttle))
				: delay > 0
					? source.Delay(TimeSpan.FromMilliseconds(delay))
					: throttle > 0
						? source.Throttle(TimeSpan.FromMilliseconds(throttle))
						: source
			).Subscribe(data => onNext(data).Run());

		/// <summary>
		/// Subscribes an element handler, and an exception handler to an observable sequence.
		/// </summary>
		/// <typeparam name="T">The type of the elements in the source sequence.</typeparam>
		/// <param name="source">Observable sequence to subscribe to.</param>
		/// <param name="onNext">Action to invoke for each element in the observable sequence.</param>
		/// <param name="onError">Action to invoke upon exceptional termination of the observable sequence.</param>
		/// <param name="delay">The times (miliseconds) for delaying.</param>
		/// <param name="throttle">The times (miliseconds) for throttling.</param>
		/// <returns><see cref="IDisposable"/> object used to unsubscribe from the observable sequence.</returns>
		/// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="onNext"/> or <paramref name="onError"/> is <c>null</c>.</exception>
		public static IDisposable Subscribe<T>(this IObservable<T> source, Func<T, Task> onNext, Action<Exception> onError, int delay = 0, int throttle = 0)
			=> (delay > 0 && throttle > 0
				? source.Delay(TimeSpan.FromMilliseconds(delay)).Throttle(TimeSpan.FromMilliseconds(throttle))
				: delay > 0
					? source.Delay(TimeSpan.FromMilliseconds(delay))
					: throttle > 0
						? source.Throttle(TimeSpan.FromMilliseconds(throttle))
						: source
			).Subscribe(data => onNext(data).Run(onError), onError);

		/// <summary>
		/// Subscribes an element handler, and an exception handler to an observable sequence.
		/// </summary>
		/// <typeparam name="T">The type of the elements in the source sequence.</typeparam>
		/// <param name="source">Observable sequence to subscribe to.</param>
		/// <param name="onNext">Action to invoke for each element in the observable sequence.</param>
		/// <param name="onError">Action to invoke upon exceptional termination of the observable sequence.</param>
		/// <param name="delay">The times (miliseconds) for delaying.</param>
		/// <param name="throttle">The times (miliseconds) for throttling.</param>
		/// <returns><see cref="IDisposable"/> object used to unsubscribe from the observable sequence.</returns>
		/// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="onNext"/> or <paramref name="onError"/> is <c>null</c>.</exception>
		public static IDisposable Subscribe<T>(this IObservable<T> source, Func<T, Task> onNext, Func<Exception, Task> onError, int delay = 0, int throttle = 0)
			=> (delay > 0 && throttle > 0
				? source.Delay(TimeSpan.FromMilliseconds(delay)).Throttle(TimeSpan.FromMilliseconds(throttle))
				: delay > 0
					? source.Delay(TimeSpan.FromMilliseconds(delay))
					: throttle > 0
						? source.Throttle(TimeSpan.FromMilliseconds(throttle))
						: source
			).Subscribe(data => onNext(data).Run(onError), ex => onError(ex).Run());
		#endregion

		#region ReactiveX WAMP extensions
		/// <summary>
		/// Registers a service into a WAMP channel.
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="instanceProvider">A delegate that creates an instance per call.</param>
		/// <param name="interceptor">An object which allows registration customization.</param>
		/// <returns>A task that is completed when all methods are registered - its result is a
		/// <see cref="IAsyncDisposable"/>- disposing it will unregister the instance.</returns>
		public static Task<IAsyncDisposable> RegisterAsync<T>(this IWampChannel channel, Func<T> instanceProvider, ICalleeRegistrationInterceptor interceptor) where T : class
			=> channel == null ? Task.FromResult<IAsyncDisposable>(null) : channel.RealmProxy.Services.RegisterCallee(instanceProvider, interceptor);

		/// <summary>
		/// Gets a service from a WAMP channel.
		/// </summary>
		/// <param name="interceptor">An object which allows call customization.</param>
		/// <typeparam name="T"></typeparam>
		/// <returns>The proxy to the callee.</returns>
		public static T GetService<T>(this IWampChannel channel, ICalleeProxyInterceptor interceptor) where T : class
			=> channel?.RealmProxy.Services.GetCalleeProxy<T>(interceptor);

		/// <summary>
		/// Subscribes an element handler to a WAMP channel's topic.
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="channel">The WAMP channel for subscribing.</param>
		/// <param name="topicURI">URI of a topic.</param>
		/// <param name="onNext">Action to invoke for each element in the observable sequence.</param>
		/// <param name="delay">The times (miliseconds) for delaying.</param>
		/// <param name="throttle">The times (miliseconds) for throttling.</param>
		/// <returns><see cref="IDisposable"/> object used to unsubscribe from the observable sequence.</returns>
		/// <exception cref="ArgumentNullException"><paramref name="channel"/> or <paramref name="onNext"/> is <c>null</c>.</exception>
		public static IDisposable Subscribe<T>(this IWampChannel channel, string topicURI, Action<T> onNext, int delay = 0, int throttle = 0)
			=> channel?.RealmProxy.Services.GetSubject<T>(topicURI).Subscribe(onNext, delay, throttle);

		/// <summary>
		/// Subscribes an element handler to a WAMP channel's topic.
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="channel">The WAMP channel for subscribing.</param>
		/// <param name="topicURI">URI of a topic.</param>
		/// <param name="onNext">Action to invoke for each element in the observable sequence.</param>
		/// <param name="delay">The times (miliseconds) for delaying.</param>
		/// <param name="throttle">The times (miliseconds) for throttling.</param>
		/// <returns><see cref="IDisposable"/> object used to unsubscribe from the observable sequence.</returns>
		/// <exception cref="ArgumentNullException"><paramref name="channel"/> or <paramref name="onNext"/> or <paramref name="onError"/> is <c>null</c>.</exception>
		public static IDisposable Subscribe<T>(this IWampChannel channel, string topicURI, Action<T> onNext, Action<Exception> onError, int delay = 0, int throttle = 0)
			=> channel?.RealmProxy.Services.GetSubject<T>(topicURI).Subscribe(onNext, onError, delay, throttle);

		/// <summary>
		/// Subscribes an element handler to a WAMP channel's topic.
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="channel">The WAMP channel for subscribing.</param>
		/// <param name="topicURI">URI of a topic.</param>
		/// <param name="onNext">Action to invoke for each element in the observable sequence.</param>
		/// <param name="delay">The times (miliseconds) for delaying.</param>
		/// <param name="throttle">The times (miliseconds) for throttling.</param>
		/// <returns><see cref="IDisposable"/> object used to unsubscribe from the observable sequence.</returns>
		/// <exception cref="ArgumentNullException"><paramref name="channel"/> or <paramref name="onNext"/> is <c>null</c>.</exception>
		public static IDisposable Subscribe<T>(this IWampChannel channel, string topicURI, Func<T, Task> onNext, int delay = 0, int throttle = 0)
			=> channel?.RealmProxy.Services.GetSubject<T>(topicURI).Subscribe(onNext, delay, throttle);

		/// <summary>
		/// Subscribes an element handler to a WAMP channel's topic.
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="channel">The WAMP channel for subscribing.</param>
		/// <param name="topicURI">URI of a topic.</param>
		/// <param name="onNext">Action to invoke for each element in the observable sequence.</param>
		/// <param name="delay">The times (miliseconds) for delaying.</param>
		/// <param name="throttle">The times (miliseconds) for throttling.</param>
		/// <returns><see cref="IDisposable"/> object used to unsubscribe from the observable sequence.</returns>
		/// <exception cref="ArgumentNullException"><paramref name="channel"/> or <paramref name="onNext"/> or <paramref name="onError"/> is <c>null</c>.</exception>
		public static IDisposable Subscribe<T>(this IWampChannel channel, string topicURI, Func<T, Task> onNext, Action<Exception> onError, int delay = 0, int throttle = 0)
			=> channel?.RealmProxy.Services.GetSubject<T>(topicURI).Subscribe(onNext, onError, delay, throttle);

		/// <summary>
		/// Subscribes an element handler to a WAMP channel's topic.
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="channel">The WAMP channel for subscribing.</param>
		/// <param name="topicURI">URI of a topic.</param>
		/// <param name="onNext">Action to invoke for each element in the observable sequence.</param>
		/// <param name="delay">The times (miliseconds) for delaying.</param>
		/// <param name="throttle">The times (miliseconds) for throttling.</param>
		/// <returns><see cref="IDisposable"/> object used to unsubscribe from the observable sequence.</returns>
		/// <exception cref="ArgumentNullException"><paramref name="channel"/> or <paramref name="onNext"/> or <paramref name="onError"/> is <c>null</c>.</exception>
		public static IDisposable Subscribe<T>(this IWampChannel channel, string topicURI, Func<T, Task> onNext, Func<Exception, Task> onError, int delay = 0, int throttle = 0)
			=> channel?.RealmProxy.Services.GetSubject<T>(topicURI).Subscribe(onNext, onError, delay, throttle);
		#endregion

	}
}