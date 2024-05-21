﻿using Eclypses.MteHttpClient.Models;
using Eclypses.MteHttpClient.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Web;

namespace Eclypses.MteHttpClient
{
    /*
     * To build a NuGet package that includes the Mte.ts file (should only be done for a cloud build)
     * you need to do the following:
     *  - include the NuGet package Microsoft.TypeScript.MSBuild
     *  - include a tsconfig.json file in your root
     *  - make sure that the csproj file includes wwwroot\Mte.js, not Mte.ts in the Content ItemGroup
     *  - make sure that the csproj file includes wwwroot\mterelay-helper.js in the Content ItemGroup
     *  - make sure that the constanst file has the proper NUGET_CONTENT_ROOT ( "./_content/Eclypses.MteHttpClient.Blazor.AWS")
     *  - make sure that the mterelay-helper.js file loads the Mte.js file from "./Mte.js"   
     */

    /// <summary>
    /// The client side MteHttpClient that communicates with an Mte-Relay proxy server.
    /// </summary>
    public class MteHttpClient : IMteHttpClient
    {
        /// <summary>
        /// This is a flag to allow you to test retry logic by simulating a 560 return code
        /// which causes this to re-pair a single pair. It is incremented in the MteGetAsync method
        /// when the documented line of code is uncommented. Leave this at '0' unless you wish
        /// to artifically test the retry logic.
        /// </summary>
        private static int testRetryAttempts = 0;
        /// <summary>
        /// Once WASM is initialized, this is true.  In the case of a page
        /// reload, pairing must be restarted, so when this is false
        /// Initialize will attempt to pair.
        /// </summary>
        private static bool _mteWASMIsInitialized = false;
        /// <summary>
        /// Ensures that Json serialization is case insensitive.
        /// </summary>
        private static JsonSerializerOptions _jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        /// <summary>
        /// Cache for the MteState(s) in this object.
        /// </summary>
        private ConcurrentDictionary<string, string> _mteStateDictionary { get; set; }
        /// <summary>
        /// The factory created actual HttpClient that communicates with the Mte-Relay.
        /// </summary>
        private IHttpClientFactory _httpClientFactory;
        /// <summary>
        /// A factory method for creating Kyber objects used in pairing "x_mte_relay_id" designated Mte objects.
        /// </summary>
        private readonly IKyberFactory _kyberFactory;
        /// <summary>
        /// Methods that wrap the java script calls into the Mte WASM module.
        /// </summary>
        private readonly IMteHelperMethods _mteHelper;
        /// <summary>
        /// The run time options for the Mte-Relay from the appSettings.json file.
        /// </summary>
        private readonly MteRelayOptions _mteRelayOptions;
        /// <summary>
        /// Standard .Net logger.
        /// </summary>
        private readonly ILogger<MteHttpClient> _logger;

        #region Constructor
        /// <summary>
        /// Constructs a new MteHttpClient that exposes
        /// many of the methods of a standard HttpClient,
        /// but protects them with the MKE and sends them
        /// to an Mte-Relay server which forwards them
        /// on to your actual API.
        /// </summary>
        /// <param name="logger">.Net logger.</param>
        /// <param name="mteRelayOptions">Run time options from appSettings.</param>
        /// <param name="mteHelperMethods">Wrapper to the MTE WASM object.</param>
        /// <param name="kyberFactory">Wrapper to the MTE Kyber object.</param>
        /// <param name="httpClientFactory">Client factory to make an HttpClient.</param>
        public MteHttpClient(
            ILogger<MteHttpClient> logger,
            MteRelayOptions mteRelayOptions,
            IMteHelperMethods mteHelperMethods,
            IKyberFactory kyberFactory,
            IHttpClientFactory httpClientFactory
           )
        {
            _logger = logger;
            _mteRelayOptions = mteRelayOptions;
            _mteHelper = mteHelperMethods;
            _kyberFactory = kyberFactory;
            _httpClientFactory = httpClientFactory;
            _mteStateDictionary = new ConcurrentDictionary<string, string>();
        }
        #endregion

        #region public InitializeAsync
        /// <summary>
        /// Initializes the Mte WASM runtime and sets the license.
        /// The endpoint is then paired with the MteRelay.
        /// This also calls an optional verification method that can send
        /// an "echo" route to your API. 
        /// </summary>
        /// <param name="relayIdentifier">Name of the relay you wish to initialize.
        /// If left blank, the first one in your list of configured MteRelays is used.</param>
        /// <param name="shouldReset">If true, all data for an existing endpoint is cleared.</param>
        /// <returns>True if successful</returns>
        /// <exception cref="ApplicationException"></exception>
        public async Task<bool> InitializeAsync(string relayIdentifier, bool shouldReset = false)
        {
            MteRelayEndpoint? endpoint = GetEndpointFromId(relayIdentifier);
            if (endpoint is null)
            {
                throw new ApplicationException($"No Endpoint is configured for {relayIdentifier}.");
            }
            return await InitializeAsync(endpoint, shouldReset);
        }

        /// <summary>
        /// Initializes the Mte WASM runtime and sets the license.
        /// The endpoint is then paired with the MteRelay.
        /// This also calls an optional verification method that can send
        /// an "echo" route to your API. 
        /// </summary>
        /// <param name="endpoint">The specific endpoint you wish to initialize.</param>
        /// <param name="shouldReset">If true, all data for an existing endpoint is cleared.</param>
        /// <returns>True if succesful.</returns>
        /// <exception cref="ApplicationException"></exception>
        /// <exception cref="Exception"></exception>
        public async Task<bool> InitializeAsync(MteRelayEndpoint endpoint, bool shouldReset = false)
        {
            try
            {
                //
                // Is WASM setup? If not, set it up.
                //
                if (!_mteWASMIsInitialized)
                {
                    //
                    // Setup the WASM Runtime and the initial cache of Encoders and Decoders.
                    //
                    await _mteHelper.SetupRuntimeAsync();
                    _mteWASMIsInitialized = true;
                }
                //
                // If shouldReset, clear the data for this endpoint.
                //
                if (shouldReset)
                {
                    endpoint.Reset();
                }
                //
                // Is this endpoint already paired?  If not, try the following:
                //
                if (!endpoint.IsPaired)
                {
                    CreateThePairIds(endpoint);
                    //
                    // Make sure we have the actual HttpClient setup.
                    //
                    CreateMteRelayClient(endpoint.HttpClientRelayName);
                    //
                    // Is this an MteRelay endpoint?  If not, this is an error.
                    // (The method that checks throws an exception if not valid.)
                    //
                    await CheckForMTERelayProxy(endpoint);
                    //
                    // If it is a valid endpoint, try to pair with it. If pairing fails, this is an error.
                    // (The method that pairs throws an exception if not valid.)
                    //
                    endpoint.IsPaired = await PairWithTheMTERelay(endpoint);

                    //
                    // Is this endpoint verified?  If not verify it.
                    //
                    if (!endpoint.IsVerified)
                    {
                        endpoint.IsVerified = await VerifyMteRelay(endpoint);
                    }
                }

                return endpoint.IsPaired;
            }
            catch (Exception ex)
            {
                //
                // If we encountered an exception, make sure this endpoint is not valid.
                //
                endpoint.IsPaired = false;
                endpoint.IsVerified = false;
                throw new Exception($"Could not setup the Mte-Relay proxy for relayId {endpoint.HttpClientRelayName}.", ex);
            }
        }
        #endregion public InitializeAsync     

        #region public SetAuthenticationHeader
        /// <summary>
        /// May be used as a convenience method to add an Authentication header for
        /// the first (and possibly only) relay identifier in your list.
        /// </summary>
        /// <param name="scheme">The authentication scheme such as 'basic' or 'bearer'.</param>
        /// <param name="value">The actual value for the authenication token.</param>
        public void SetAuthenticationHeader(string scheme, string value)
        {
            SetAuthenticationHeader(string.Empty, scheme, value);
        }
        /// <summary>
        /// May be used as a convenience method to add an Authentication header for a specific relay identifier.
        /// </summary>
        /// <param name="relayIdentifier">The identifier for the endpoint you configured in your appsettings.</param>
        /// <param name="scheme">The authentication scheme such as 'basic' or 'bearer'.</param>
        /// <param name="value">The actual value for the authenication token.</param>
        public void SetAuthenticationHeader(string relayIdentifier, string scheme, string value)
        {
            var endpoint = GetEndpointFromId(relayIdentifier);
            if (endpoint is not null)
            {
                if (endpoint.AuthenticationHeader is null)
                {
                    endpoint.AuthenticationHeader = new MteAuthenticationHeaderValue();
                }
                endpoint.AuthenticationHeader.Set(scheme, value);
            }
        }
        #endregion

        #region public SetDefaultRequestHeader
        /// <summary>
        /// A convenience method to set request headers for a specific relay identifier.  These will be included
        /// in each and every request. If this already exists, the value is replaced. If the value is empty,
        /// this header is removed.
        /// </summary>
        /// <param name="relayIdentifier">The identifier for the endpoint you configured in your appsettings.</param>
        /// <param name="key">The key for this speicific header.</param>
        /// <param name="value">The value for this specific header.</param>
        public void SetDefaultRequestHeader(string relayIdentifier, string key, string value = "")
        {
            var endpoint = GetEndpointFromId(relayIdentifier);
            if (endpoint is not null)
            {
                if (endpoint.MteDefaultRequestHeaders is null)
                {
                    endpoint.MteDefaultRequestHeaders = new Dictionary<string, string>();
                }
                if (endpoint.MteDefaultRequestHeaders.ContainsKey(key))
                {
                    //
                    // If we were given an empty value, remove this header.
                    //
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        endpoint.MteDefaultRequestHeaders.Remove(key);
                    }
                    else
                    {
                        endpoint.MteDefaultRequestHeaders[key] = value;
                    }
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        endpoint.MteDefaultRequestHeaders.Add(key, value);
                    }
                }
            }
        }

        /// <summary>
        /// A convenience method to set request headers for a the first (and possibly only relay identifier).  These will be included
        /// in each and every request. If this already exists, the value is replaced. If the value is empty,
        /// this header is removed.
        /// </summary>
        /// <param name="relayIdentifier">The identifier for the endpoint you configured in your appsettings.</param>
        /// <param name="key">The key for this speicific header.</param>
        /// <param name="value">The value for this specific header.</param>
        public void SetDefaultRequestHeader(string key, string value = "")
        {
            SetDefaultRequestHeader(string.Empty, key, value);
        }
        #endregion SetDefaultRequestHeader

        #region public MteGetAsync
        /// <summary>
        /// Sends an ASP.Net GetAsync through the Mte Relay endpoint server.
        /// </summary>
        /// <param name="route">The specific route within the ultimate API that you wish to communicate with.</param>
        /// <param name="headers">Optional list of custom headers to add.</param>
        /// <param name="relayIdentifier">The identifier for the endpoint you configured in your appsettings.</param>
        /// <param name="protectionOptions">Optional override options to manage protection of URL and Headers.</param>
        /// <param name="getAttempts">Number of times to attempt the GET request before failing.</param>
        /// <returns>HttpResponseMessage from the GET.</returns>
        /// <exception cref="ApplicationException"></exception>
        /// <exception cref="Exception"></exception>
        public async Task<HttpResponseMessage> MteGetAsync(
            string route,
            Dictionary<string, string>? headers = null,
            string? relayIdentifier = "",
            MteProtectionOptions? protectionOptions = null,
            int getAttempts = 0)
        {
            MteRelayEndpoint endpoint = CreateMteRelayClient(relayIdentifier);
            HttpResponseMessage? relayResponse = null;
            try
            {
                if (getAttempts == 0) getAttempts = Constants.MAX_PROXY_RETRY_ATTEMPTS;
                //
                // Ensure that WASM has been initialized - if the page was reloaded, it 
                // needs to re-init.
                //
                if (!await InitializeAsync(endpoint))
                {
                    throw new ApplicationException("The Mte-Relay could not be verified - perhaps WASM is corrupted.");
                }
                //
                // Make sure the request has a valid PairId
                // If this is a retry, the MTE_RELAY_HEADER is already present
                // but for the bad pair, so remove it from the headers
                // prior to "getting the next pair id".
                //
                if (headers is null)
                {
                    headers = new Dictionary<string, string>();
                }
                //
                // Clear the old x-mte-relay header.
                //
                headers.Remove(Constants.MTE_RELAY_HEADER);
                endpoint.XmteRelayHeader.PairId = GetNextPairId(endpoint);
                //
                // Check to see if there are any request overrides for the protection options.
                //
                MteProtectionOptions resolvedProtectionOptions = ResolveRequestLevelProtectionOverrides(protectionOptions, _mteRelayOptions);
                //
                // If we want to protect the route and query string, do that here.               
                //
                if (resolvedProtectionOptions.ShouldProtectUrl!.Value)
                {
                    route = await MakeEncodedRoute(endpoint.XmteRelayHeader.PairId, route);
                }
                //
                // Create the encoded header collection (includes the content-type header).
                //
                (Dictionary<string, string>? clearHeaders, string? encodedHeaders) = await MakeEncodedPayloadHeader(endpoint, HttpMethod.Get, headers, "text/plain", resolvedProtectionOptions);

                //
                // Ensure that the required header is present.
                //
                clearHeaders!.Add(Constants.MTE_RELAY_HEADER, MakeMteRelayHeaderValue(endpoint, resolvedProtectionOptions, true, !string.IsNullOrEmpty(encodedHeaders), false));

                // https://www.codeproject.com/Articles/5363405/Blazor-WASM-Hosted-App-with-Cookie-based-Authentic # Does this work?
                //
                // Make a request to send to the relay server.
                //
                HttpRequestMessage request = MakeHttpRelayRequest(null, route, HttpMethod.Get, encodedHeaders!, clearHeaders);
                //
                // Increment the attempt counter and send the request to the relay.
                //
                endpoint.RetryAttempts++;
                //
                // To test retry logic when debugging, uncomment this next line.
                //
                // testRetryAttempts++;                          
                //
                // Send the protected message to the Mte-Relay endpointInformation.
                //
                relayResponse = await endpoint.mteRelayClient!.SendAsync(request, HttpCompletionOption.ResponseContentRead);
                //
                // Convert the relayResponse to a clearResponse for the application to consume.
                //
                await CreateDecodedHttpResponseMessage(endpoint.XmteRelayHeader.PairId, relayResponse);
                return relayResponse;
            }
            catch (MteRelayException mrex)
            {
                if (endpoint.RetryAttempts <= getAttempts)
                {
                    var retryResponse = await MteGetAsync(route, headers, relayIdentifier, protectionOptions, getAttempts);
                    //
                    // This will remove the failed mte states and request
                    // a new pair id from the MTERelay endpointInformation server. It is an
                    // async method, but we do not wish to await it so that
                    // it runs on a separate thread and does not block the main
                    // application.
                    //
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                    await ReplacePairedEndpoint(endpoint);
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                    return retryResponse;
                }
                _logger.LogError($"Retried sending to {route} {getAttempts - 1} times and was not successful: {mrex.Message}");
                endpoint.RetryAttempts = 0;
                //
                // If we tried the MAX_PROXY_RETRY_ATTEMPTS and is still failed,
                // return the HttpResponse of the original endpointInformation request
                // so that the application can handle it
                // (note: this will be a 500 status code).
                //
                return relayResponse!;
            }
            catch (Exception ex)
            {
                throw new Exception($"MteGetAsync failed to {route}", ex);
            }
        }
        #endregion public MteGetAsync        

        #region public MteGetStringAsync
        /// <summary>
        /// Sends an ASP.Net GetStringAsync through the Mte Relay endpoint server.
        /// </summary>
        /// <param name="route">The specific route within the ultimate API that you wish to communicate with.</param>
        /// <param name="headers">Optional list of custom headers to add.</param>
        /// <param name="relayIdentifier">The identifier for the endpoint you configured in your appsettings.</param>
        /// <param name="protectionOptions">Optional override options to manage protection of URL and Headers.</param>
        /// <param name="getAttempts">Number of times to attempt the GET request before failing.</param>
        /// <returns>String of what was received from the API (after being protected with Mte Relay).</returns>
        public async Task<string> MteGetStringAsync(
            string route,
            Dictionary<string, string>? headers = null,
            string? relayIdentifier = "",
            MteProtectionOptions? protectionOptions = null,
            int getAttempts = 0)
        {
            HttpResponseMessage? responseMessage = null;
            try
            {
                responseMessage = await MteGetAsync(route, headers, relayIdentifier, protectionOptions, getAttempts);
                responseMessage.EnsureSuccessStatusCode();
                return await responseMessage.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                throw new Exception($"MteGetStringAsync failed to {route}: Response status code does not indicate success: {responseMessage!.StatusCode}", ex);
            }
        }
        #endregion MteGetStringAsync

        #region public MteGetByteArrayAsync
        /// <summary>
        /// Sends an ASP.Net GetByteArrayAsync through the Mte Relay endpointInformation server.
        /// </summary>
        /// <param name="route">The specific route within the ultimate API that you wish to communicate with.</param>
        /// <param name="headers">Optional list of custom headers to add.</param>
        /// <param name="relayIdentifier">The identifier for the endpoint you configured in your appsettings.</param>
        /// <param name="protectionOptions">Optional override options to manage protection of URL and Headers.</param>
        /// <param name="getAttempts">Number of times to attempt the GET request before failing.</param>
        /// <returns>Byte array of what was received from the API (after being protected with Mte Relay).</returns>
        public async Task<byte[]> MteGetByteArrayAsync(
            string route,
            Dictionary<string, string>? headers = null,
            string? relayIdentifier = "",
            MteProtectionOptions? protectionOptions = null,
            int getAttempts = 0)
        {
            HttpResponseMessage? responseMessage = null;
            try
            {
                responseMessage = await MteGetAsync(route, headers, relayIdentifier, protectionOptions, getAttempts);
                responseMessage.EnsureSuccessStatusCode();
                return await responseMessage.Content.ReadAsByteArrayAsync();
            }
            catch (Exception ex)
            {
                throw new Exception($"MteGetByteArrayAsync failed to {route} Response status code does not indicate success: {responseMessage!.StatusCode}", ex);
            }
        }
        #endregion MteGetByteArrayAsync

        #region public MteGetStreamAsync
        /// <summary>
        /// Not available at this time.
        /// </summary>
        /// <param name="route"></param>
        /// <param name="headers"></param>
        /// <returns></returns>
        /// <exception cref="NotSupportedException"></exception>
#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        public async Task<Stream> MteGetStreamAsync(string route, Dictionary<string, string>? headers = null)
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
        {
            try
            {
                throw new NotSupportedException("MteGetStream is not a supported method - use MteGetByteArrayAsync instead.");
            }
            catch (Exception ex)
            {
                throw new Exception($"MteGetStreamAsync failed to {route}", ex);
            }
        }
        #endregion MteGetStreamAsync

        #region public MtePostAsync 
        /// <summary>
        /// Sends an ASP.Net PostAsync through the Mte Relay endpointInformation server.
        /// </summary>
        /// <param name="route">The specific route within the ultimate API that you wish to communicate with.</param>
        /// <param name="content">An HttpContent object to POST to the eventual API.</param>
        /// <param name="headers">Optional list of custom headers to add.</param>
        /// <param name="relayIdentifier">The identifier for the endpoint you configured in your appsettings.</param> 
        /// <param name="protectionOptions">Optional override options to manage protection of URL and Headers.</param>
        /// <param name="postAttempts">Number of times to retry a failed POST attempt.</param>
        /// <returns>HttpResponseMessage of what was received from the API (after being protected with Mte Relay).</returns>
        public async Task<HttpResponseMessage> MtePostAsync(string route,
            HttpContent? content,
            Dictionary<string, string>? headers = null,
            string? relayIdentifier = "",
            MteProtectionOptions? protectionOptions = null,
            int postAttempts = 0)
        {
            MteRelayEndpoint endpoint = CreateMteRelayClient(relayIdentifier);
            HttpResponseMessage? relayResponse = null;
            if (postAttempts == 0) postAttempts = Constants.MAX_PROXY_RETRY_ATTEMPTS;
            try
            {
                //
                // Ensure that WASM has been initialized - if the page was reloaded, it 
                // needs to re-init.
                //
                if (!await InitializeAsync(endpoint))
                {
                    throw new ApplicationException("The Mte-Relay could not be verified - perhaps WASM is corrupted.");
                }
                //
                // Make sure the request has a valid PairId
                // If this is a retry, the RELAY_PAIR_ID_HEADER is already present
                // but for the bad pair, so remove it from the headers
                // prior to "getting the next pair id".
                //
                if (headers is null)
                {
                    headers = new Dictionary<string, string>();
                }
                headers.Remove(Constants.MTE_RELAY_HEADER);
                endpoint.XmteRelayHeader.PairId = GetNextPairId(endpoint);
                //
                // Check to see if there are any request overrides for the protection options.
                //
                MteProtectionOptions resolvedProtectionOptions = ResolveRequestLevelProtectionOverrides(protectionOptions, _mteRelayOptions);
                //
                // If we want to protect the route and query string, do that here.               
                //
                if (resolvedProtectionOptions.ShouldProtectUrl!.Value)
                {
                    route = await MakeEncodedRoute(endpoint.XmteRelayHeader.PairId, route);
                }
                //
                // Create the encoded header collection (includes the content-type header).
                //
                //
                // Save the original content in case we must retry.
                //
                HttpContent? originalContent = content;

                Dictionary<string, string>? clearHeaders = new Dictionary<string, string>();
                string? encodedHeaders = string.Empty;

                //
                // Create an encoded content if we received something
                // 
                if (content is not null)
                {
                    //
                    // Make the content to send to the MteRelay proxy.
                    //
                    if (content.GetType() == typeof(JsonContent))
                    {
                        (clearHeaders, encodedHeaders) = await MakeEncodedPayloadHeader(endpoint, HttpMethod.Post, headers, "application/json", resolvedProtectionOptions);
                        content = await MakeEncodedContent(await content.ReadAsStringAsync(), endpoint.XmteRelayHeader.PairId);
                    }
                    else if (content.GetType() == typeof(StringContent))
                    {
                        (clearHeaders, encodedHeaders) = await MakeEncodedPayloadHeader(endpoint, HttpMethod.Post, headers, "text/plain", resolvedProtectionOptions);
                        content = await MakeEncodedContent(await content.ReadAsStringAsync(), endpoint.XmteRelayHeader.PairId);
                    }
                    else if (content.GetType() == typeof(ByteArrayContent))
                    {
                        (clearHeaders, encodedHeaders) = await MakeEncodedPayloadHeader(endpoint, HttpMethod.Post, headers, "application/octet-stream", resolvedProtectionOptions);
                        content = await MakeEncodedContent(await content.ReadAsByteArrayAsync(), endpoint.XmteRelayHeader.PairId);
                    }
                }
                else
                {
                    (clearHeaders, encodedHeaders) = await MakeEncodedPayloadHeader(endpoint, HttpMethod.Post, headers, Constants.STR_CONTENT_NO_CONTENT, resolvedProtectionOptions);
                }
                //
                // Ensure that the required header is present.
                //
                if (content is null && string.IsNullOrWhiteSpace(encodedHeaders))
                {
                    resolvedProtectionOptions.HeaderDisposition = RelayHeaderDisposition.EncodeNoHeaders;
                }
                clearHeaders!.Add(Constants.MTE_RELAY_HEADER, MakeMteRelayHeaderValue(endpoint, resolvedProtectionOptions, hasContent: content != null));
                //
                // Make a request to send to the relay server.
                //
                HttpRequestMessage request = MakeHttpRelayRequest(content, route, HttpMethod.Post, encodedHeaders!, clearHeaders);
                //
                // Send the request to the relay.
                //
                endpoint.RetryAttempts++;
                relayResponse = await endpoint.mteRelayClient!.SendAsync(request, HttpCompletionOption.ResponseContentRead);
                //
                // Convert the relayResponse to a clearResponse for the application to consume.
                //
                await CreateDecodedHttpResponseMessage(endpoint.XmteRelayHeader.PairId, relayResponse, originalContent);
                return relayResponse;

            }
            catch (MteRelayException mrex)
            {
                if (endpoint.RetryAttempts <= postAttempts)
                {
                    //
                    // Retry this method with the original content which is a property of the MteRelayException.
                    //
                    var retryResponse = await MtePostAsync(route, mrex.OriginalContent!, headers, relayIdentifier, protectionOptions, postAttempts);
                    //
                    // This will remove the failed mte states and request
                    // a new pair id from the MTERelay endpointInformation server. It is an
                    // async method, but we do not wish to await it so that
                    // it runs on a separate thread and does not block the main
                    // application.
                    //
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                    await ReplacePairedEndpoint(endpoint);
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                    return retryResponse;
                }
                _logger.LogError($"Retried sending to {route} {postAttempts - 1} times and was not successful: {mrex.Message}");
                endpoint.RetryAttempts = 0;
                //
                // If we tried the MAX_PROXY_RETRY_ATTEMPTS and is still failed,
                // return the HttpResponse of the original endpointInformation request
                // so that the application can handle it
                // (note: this will be a 500 status code).
                //
                return relayResponse!;
            }
            catch (Exception ex)
            {
                throw new Exception($"MtePostAsync failed to {route}", ex);
            }
        }
        #endregion public MtePostAsync

        #region public MtePatchAsync 
        /// <summary>
        /// Sends an ASP.Net PatchAsync through the Mte Relay endpointInformation server.
        /// </summary>
        /// <param name="route">The specific route within the ultimate API that you wish to communicate with.</param>
        /// <param name="content">An HttpContent object to PATCH to the eventual API.</param>
        /// <param name="headers">Optional list of custom headers to add.</param>
        /// <param name="relayIdentifier">The identifier for the endpoint you configured in your appsettings.</param> 
        /// <param name="protectionOptions">Optional override options to manage protection of URL and Headers.</param>
        /// <param name="patchAttempts">Number of times to retry the PATCH request before failing.</param>
        /// <returns>HttpResponseMessage of what was received from the API (after being protected with Mte Relay).</returns>
        public async Task<HttpResponseMessage> MtePatchAsync(
            string route,
            HttpContent content,
            Dictionary<string, string>? headers = null,
            string? relayIdentifier = "",
            MteProtectionOptions? protectionOptions = null,
            int patchAttempts = 0)
        {
            MteRelayEndpoint endpoint = CreateMteRelayClient(relayIdentifier);
            HttpResponseMessage? relayResponse = null;
            try
            {
                if (patchAttempts == 0) patchAttempts = Constants.MAX_PROXY_RETRY_ATTEMPTS;
                //
                // Ensure that WASM has been initialized - if the page was reloaded, it 
                // needs to re-init.
                //
                if (!await InitializeAsync(endpoint))
                {
                    throw new ApplicationException("The Mte-Relay could not be verified - perhaps WASM is corrupted.");
                }
                //
                // Make sure the request has a valid PairId
                //
                if (headers is null)
                {
                    headers = new Dictionary<string, string>();
                }
                headers.Remove(Constants.MTE_RELAY_HEADER);
                endpoint.XmteRelayHeader.PairId = GetNextPairId(endpoint);
                //
                // Check to see if there are any request overrides for the protection options.
                //
                MteProtectionOptions resolvedProtectionOptions = ResolveRequestLevelProtectionOverrides(protectionOptions, _mteRelayOptions);
                //
                // If we want to protect the route and query string, do that here.               
                //
                if (resolvedProtectionOptions.ShouldProtectUrl!.Value)
                {
                    route = await MakeEncodedRoute(endpoint.XmteRelayHeader.PairId, route);
                }
                //
                // Save the original content in case we must retry.
                //
                HttpContent originalContent = content;
                //
                // Create the encoded header collection (includes the content-type header).
                //
                Dictionary<string, string>? clearHeaders = new Dictionary<string, string>();
                string? encodedHeaders = string.Empty;
                if (content.GetType() == typeof(JsonContent))
                {
                    (clearHeaders, encodedHeaders) = await MakeEncodedPayloadHeader(endpoint, HttpMethod.Patch, headers, "application/json", resolvedProtectionOptions);
                    content = await MakeEncodedContent(await content.ReadAsStringAsync(), endpoint.XmteRelayHeader.PairId);
                }
                else if (content.GetType() == typeof(StringContent))
                {
                    (clearHeaders, encodedHeaders) = await MakeEncodedPayloadHeader(endpoint, HttpMethod.Patch, headers, "text/plain", resolvedProtectionOptions);
                    content = await MakeEncodedContent(await content.ReadAsStringAsync(), endpoint.XmteRelayHeader.PairId);
                }
                else if (content.GetType() == typeof(ByteArrayContent))
                {
                    (clearHeaders, encodedHeaders) = await MakeEncodedPayloadHeader(endpoint, HttpMethod.Patch, headers, "application/octet-stream", resolvedProtectionOptions);
                    content = await MakeEncodedContent(await content.ReadAsByteArrayAsync(), endpoint.XmteRelayHeader.PairId);
                }
                //
                // Ensure that the required header is present.
                //
                clearHeaders!.Add(Constants.MTE_RELAY_HEADER, MakeMteRelayHeaderValue(endpoint, resolvedProtectionOptions));
                //
                // Make a request to send to the relay server.
                //
                HttpRequestMessage request = MakeHttpRelayRequest(content, route, HttpMethod.Patch, encodedHeaders!, clearHeaders);
                //
                // Send the request to the relay.
                //
                endpoint.RetryAttempts++;
                relayResponse = await endpoint.mteRelayClient!.SendAsync(request, HttpCompletionOption.ResponseContentRead);
                //
                // Convert the relay relayResponse to a clear relayResponse for the application to consume.
                //
                await CreateDecodedHttpResponseMessage(endpoint.XmteRelayHeader.PairId, relayResponse, originalContent);
                return relayResponse;
            }
            catch (MteRelayException mrex)
            {
                if (endpoint.RetryAttempts <= patchAttempts)
                {
                    //
                    // Retry this method with the original content which is a property of the MteRelayException.
                    //
                    var retryResponse = await MtePatchAsync(route, mrex.OriginalContent!, headers, relayIdentifier, protectionOptions, patchAttempts);
                    //
                    // This will remove the failed mte states and request
                    // a new pair id from the MTERelay endpointInformation server. It is an
                    // async method, but we do not wish to await it so that
                    // it runs on a separate thread and does not block the main
                    // application.
                    //
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                    await ReplacePairedEndpoint(endpoint);
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                    return retryResponse;
                }
                _logger.LogError($"Retried sending to {route} {patchAttempts - 1} times and was not successful: {mrex.Message}");
                endpoint.RetryAttempts = 0;
                //
                // If we tried the MAX_PROXY_RETRY_ATTEMPTS and is still failed,
                // return the HttpResponse of the original endpointInformation request
                // so that the application can handle it
                // (note: this will be a 500 status code).
                //
                return relayResponse!;
            }
            catch (Exception ex)
            {
                throw new Exception($"MtePatchAsync failed to {route}", ex);
            }
        }
        #endregion public MtePatchAsync

        #region public MtePutAsync
        /// <summary>
        /// Sends an ASP.Net PutAsync through the Mte Relay endpointInformation server.
        /// </summary>
        /// <param name="route">The specific route within the ultimate API that you wish to communicate with.</param>
        /// <param name="content">An HttpContent object to PUT to the eventual API.</param>
        /// <param name="headers">Optional list of custom headers to add.</param>
        /// <param name="relayIdentifier">The identifier for the endpoint you configured in your appsettings.</param> 
        /// <param name="protectionOptions">Optional override options to manage protection of URL and Headers.</param>
        /// <param name="putAttempts">Number of times to retry the PUT attempt before failing.</param>
        /// <returns>HttpResponseMessage of what was received from the API (after being protected with Mte Relay).</returns>
        public async Task<HttpResponseMessage> MtePutAsync(
            string route,
            HttpContent? content,
            Dictionary<string, string>? headers = null,
            string? relayIdentifier = "",
            MteProtectionOptions? protectionOptions = null,
            int putAttempts = 0)
        {
            MteRelayEndpoint endpoint = CreateMteRelayClient(relayIdentifier);
            HttpResponseMessage? relayResponse = null;
            try
            {
                if (putAttempts == 0) putAttempts = Constants.MAX_PROXY_RETRY_ATTEMPTS;
                //
                // Ensure that WASM has been initialized - if the page was reloaded, it 
                // needs to re-init.
                //
                if (!await InitializeAsync(endpoint))
                {
                    throw new ApplicationException("The Mte-Relay could not be verified - perhaps WASM is corrupted.");
                }
                //
                // Make sure the request has a valid PairId
                // If this is a retry, the RELAY_PAIR_ID_HEADER is already present
                // but for the bad pair, so remove it from the headers
                // prior to "getting the next pair id".
                //
                if (headers is null)
                {
                    headers = new Dictionary<string, string>();
                }
                headers.Remove(Constants.MTE_RELAY_HEADER);
                endpoint.XmteRelayHeader.PairId = GetNextPairId(endpoint);
                //
                // Check to see if there are any request overrides for the protection options.
                //
                MteProtectionOptions resolvedProtectionOptions = ResolveRequestLevelProtectionOverrides(protectionOptions, _mteRelayOptions);
                //
                // If we want to protect the route and query string, do that here.               
                //
                if (resolvedProtectionOptions.ShouldProtectUrl!.Value)
                {
                    route = await MakeEncodedRoute(endpoint.XmteRelayHeader.PairId, route);
                }
                //
                // Save the original content in case we must retry.
                //
                HttpContent? originalContent = content;
                //
                // Create the encoded header collection (includes the content-type header).
                //
                Dictionary<string, string>? clearHeaders = new Dictionary<string, string>();
                string? encodedHeaders = string.Empty;

                if (content is not null)
                {

                    if (content.GetType() == typeof(JsonContent))
                    {
                        (clearHeaders, encodedHeaders) = await MakeEncodedPayloadHeader(endpoint, HttpMethod.Put, headers, "application/json", resolvedProtectionOptions);
                        content = await MakeEncodedContent(await content.ReadAsStringAsync(), endpoint.XmteRelayHeader.PairId);
                    }
                    else if (content.GetType() == typeof(StringContent))
                    {
                        (clearHeaders, encodedHeaders) = await MakeEncodedPayloadHeader(endpoint, HttpMethod.Put, headers, "text/plain", resolvedProtectionOptions);
                        content = await MakeEncodedContent(await content.ReadAsStringAsync(), endpoint.XmteRelayHeader.PairId);
                    }
                    else if (content.GetType() == typeof(ByteArrayContent))
                    {
                        (clearHeaders, encodedHeaders) = await MakeEncodedPayloadHeader(endpoint, HttpMethod.Put, headers, "application/octet-stream", resolvedProtectionOptions);
                        content = await MakeEncodedContent(await content.ReadAsByteArrayAsync(), endpoint.XmteRelayHeader.PairId);
                    }
                }
                else
                {
                    (clearHeaders, encodedHeaders) = await MakeEncodedPayloadHeader(endpoint, HttpMethod.Put, headers, Constants.STR_CONTENT_NO_CONTENT, resolvedProtectionOptions);
                }
                //
                // Ensure that the required header is present.
                //
                if (content is null && string.IsNullOrWhiteSpace(encodedHeaders))
                {
                    resolvedProtectionOptions.HeaderDisposition = RelayHeaderDisposition.EncodeNoHeaders;
                }
                //
                // Ensure that the required header is present.
                //
                clearHeaders!.Add(Constants.MTE_RELAY_HEADER, MakeMteRelayHeaderValue(endpoint, resolvedProtectionOptions, hasContent: content != null));
                //
                // Make a request to send to the relay server.
                //
                HttpRequestMessage request = MakeHttpRelayRequest(content, route, HttpMethod.Put, encodedHeaders!, clearHeaders);
                //
                // Send the request to the relay.
                //
                endpoint.RetryAttempts++;
                relayResponse = await endpoint.mteRelayClient!.SendAsync(request, HttpCompletionOption.ResponseContentRead);
                //
                // Convert the relay Response to a clear relayResponse for the application to consume.
                //
                await CreateDecodedHttpResponseMessage(endpoint.XmteRelayHeader.PairId, relayResponse, originalContent);
                return relayResponse;
            }
            catch (MteRelayException mrex)
            {
                if (endpoint.RetryAttempts <= putAttempts)
                {
                    //
                    // Retry this method with the original content which is a property of the MteRelayException.
                    //
                    var retryResponse = await MtePutAsync(route, mrex.OriginalContent!, headers, relayIdentifier, protectionOptions, putAttempts);
                    //
                    // This will remove the failed mte states and request
                    // a new pair id from the MTERelay endpointInformation server. It is an
                    // async method, but we do not wish to await it so that
                    // it runs on a separate thread and does not block the main
                    // application.
                    //
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                    await ReplacePairedEndpoint(endpoint);
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                    return retryResponse;
                }
                _logger.LogError($"Retried sending to {route} {putAttempts - 1} times and was not successful: {mrex.Message}");
                endpoint.RetryAttempts = 0;
                //
                // If we tried the MAX_PROXY_RETRY_ATTEMPTS and is still failed,
                // return the HttpResponse of the original endpointInformation request
                // so that the application can handle it
                // (note: this will be a 500 status code).
                //
                return relayResponse!;
            }
            catch (Exception ex)
            {
                throw new Exception($"MtePutAsync failed to {route}", ex);
            }
        }
        #endregion public MtePutAsync

        #region private ResolveRequestLevelProtectionOverrides
        /// <summary>
        /// Resolves any request specific options to the way we protect the request to the proxy.
        /// </summary>
        /// <param name="overrideProtectionOptions">The protection overrides for this request.</param>
        /// <param name="mteRelayOptions">The configured protection values for this session (in appSettings).</param>
        /// <returns>MteProtectionOptions object where overrides have been applied.</returns>
        private MteProtectionOptions ResolveRequestLevelProtectionOverrides(MteProtectionOptions? overrideProtectionOptions, MteRelayOptions mteRelayOptions)
        {
            //
            // Start by setting the resolved options to the configured options from appSettings.
            //
            MteProtectionOptions resolvedProtectionOptions = mteRelayOptions;
            //
            // If we have an override object, apply the various properties (each of which may be null).
            //
            if (overrideProtectionOptions is not null)
            {
                if (overrideProtectionOptions.ShouldProtectUrl.HasValue)
                {
                    resolvedProtectionOptions.ShouldProtectUrl = overrideProtectionOptions.ShouldProtectUrl;
                }
                if (overrideProtectionOptions.HeaderDisposition != RelayHeaderDisposition.Unknown)
                {
                    resolvedProtectionOptions.HeaderDisposition = overrideProtectionOptions.HeaderDisposition;
                }
                //
                // If we want to override the headers list for this request, try to do that.
                //
                if (overrideProtectionOptions.HeadersToEncode is not null && overrideProtectionOptions.HeadersToEncode.Count > 0)
                {
                    if (resolvedProtectionOptions.HeadersToEncode is null)
                    {
                        resolvedProtectionOptions.HeadersToEncode = new List<string>();
                    }
                    else
                    {
                        resolvedProtectionOptions.HeadersToEncode.Clear();
                    }
                    resolvedProtectionOptions.HeadersToEncode.AddRange(overrideProtectionOptions.HeadersToEncode);
                }
                //
                // If we asked to override a list of headers, but none were supplied - encode all headers.
                //
                if (overrideProtectionOptions.HeaderDisposition == RelayHeaderDisposition.EncodeListOfHeaders && overrideProtectionOptions.HeadersToEncode!.Count == 0)
                {
                    resolvedProtectionOptions.HeaderDisposition = RelayHeaderDisposition.EncodeAllHeaders;
                }
            }
            //
            // Return the result of applying our overrides.
            //
            return resolvedProtectionOptions;
        }
        #endregion

        #region private GetEndpointFromId
        /// <summary>
        /// Returns the MteRelayEndpoint object given the specific EndpointIdentifier.
        /// </summary>
        /// <param name="relayIdentifier">The identifier for the endpoint you configured in your appsettings.</param> 
        /// <returns>A configured endpoint.</returns>
        /// <exception cref="ApplicationException"></exception>
        private MteRelayEndpoint? GetEndpointFromId(string relayIdentifier)
        {
            if (_mteRelayOptions.Endpoints is null || _mteRelayOptions.Endpoints.Count == 0)
            {
                throw new ApplicationException("No Mte-Relay endpoints are configured.");
            }

            if (string.IsNullOrEmpty(relayIdentifier))
            {
                return _mteRelayOptions.Endpoints.FirstOrDefault();
            }

            var endpoint = _mteRelayOptions.Endpoints.Where(e => e.HttpClientRelayName!.Equals(relayIdentifier, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
            if (endpoint is null)
            {
                throw new ApplicationException($"Your requested endpoint identifier ({relayIdentifier}) is not one of your configured endpoints.");
            }
            return endpoint;
        }
        #endregion

        #region private CreateMteRelayClient
        /// <summary>
        /// Creates an named HttpClient from the HttpClientFactory for the named identifier.
        /// </summary>
        /// <param name="relayIdentifier">The identifier for the endpoint you configured in your appsettings.</param> 
        /// <returns>The MteRelayEndpoint object with the actual created client.</returns>
        /// <exception cref="ApplicationException"></exception>
        private MteRelayEndpoint CreateMteRelayClient(string? relayIdentifier)
        {
            try
            {
                MteRelayEndpoint? endpoint = _mteRelayOptions.Endpoints!.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(relayIdentifier))
                {
                    endpoint = GetEndpointFromId(relayIdentifier);
                }
                if (endpoint is null)
                {
                    throw new ApplicationException($"You have not configured an Mte-Relay endpoint with the name of {relayIdentifier}.");
                }

                if (endpoint.mteRelayClient is null)
                {
                    endpoint.mteRelayClient = _httpClientFactory.CreateClient(endpoint.HttpClientRelayName!);
                    endpoint.mteRelayClient.BaseAddress = new Uri(endpoint.MteRelayUrl!);
                }                
                return endpoint;
            }
            catch (Exception ex)
            {
                throw;
            }
        }
        #endregion

        #region private CreateThePairIds
        /// <summary>
        /// Ensures that the PairId list for a specific endpoint
        /// contains new unique pair identifiers.
        /// </summary>
        /// <param name="endpoint">The specific endpoint you are creating the MteState pairs for.</param>
        private void CreateThePairIds(MteRelayEndpoint endpoint)
        {
            if (endpoint.PairIds!.Count == 0)
            {
                //
                // Based on the number of pairs in the MteRelayOptions object
                // create a list of unique pair ids.  These are the
                // key to a concurrent dictionary of MteEncoder and MteDecoder states.
                //        
                for (int item = 0; item < _mteRelayOptions.NumberOfConcurrentMteStates; item++)
                {
                    endpoint.PairIds.Add(Guid.NewGuid().ToString());
                }
            }
        }
        #endregion

        #region private VerifyMteRelay
        /// <summary>
        /// If you have configured an ApiEchoRoute in your
        /// MteRelayOptions for this endpoint, this will send a GET string
        /// request to that route (via the Mte-Relay).
        /// This allows you to know if your API is available and listening.
        /// </summary>
        /// <param name="endpoint">The specific endpoint you wish to verify.</param>
        /// <returns>True if the API is listening.</returns>
        /// <exception cref="ApplicationException"></exception>
        private async Task<bool> VerifyMteRelay(MteRelayEndpoint endpoint)
        {
            if (endpoint.IsVerified)
            {
                return true;
            }
            bool isVerified = false;
            //
            // If a ApiEchoRoute is found, do a "MteGetStringAsync" to check
            // if the actual API is available.  If none is found, skip this check and return true.
            //
            if (!string.IsNullOrWhiteSpace(endpoint.ApiEchoRoute))
            {
                //
                // Verify that the API is actually online by echoing your API server
                // using the MteHttpClient's method of MteGetStringAsync.
                // 
                string echoString = await MteGetStringAsync(endpoint.ApiEchoRoute, relayIdentifier: endpoint.HttpClientRelayName);
                //
                // We must get an echoed string back from the "apiEchoRoute"
                // since it went through the MteRelay endpointInformation.
                //
                if (string.IsNullOrEmpty(echoString))
                {
                    throw new ApplicationException($"{endpoint.ApiEchoRoute} did not complete - MteRelay or API is not available for {endpoint.HttpClientRelayName}.");
                }
                //
                // If no handshake string is configured 
                // return true since we already checked for the 
                // existence of a returned echo.
                //
                if (string.IsNullOrWhiteSpace(endpoint.ApiEchoString))
                {
                    isVerified = true;
                }
                else
                {
                    //
                    // If we have a handshake string, check to
                    // see if the returned echo has it embedded
                    // and only return true if if is found.
                    //
                    if (echoString.Contains(endpoint.ApiEchoString, StringComparison.OrdinalIgnoreCase))
                    {
                        isVerified = true;
                    }
                    else
                    {
                        throw new ApplicationException($"The route at {endpoint.ApiEchoRoute} for {endpoint.HttpClientRelayName} should have returned a string containing {endpoint.ApiEchoString}, but it did not.");
                    }
                }
            }
            else
            {
                //
                // If we do not supply an echo route, assume it is verified.
                //
                isVerified = true;
            }
            return isVerified;
        }
        #endregion

        #region private GetNextPairId
        /// <summary>
        /// Gets the next available x_mte_relay_id in a round-robin fashion
        /// for use in determining which Encoder / Decoder pair that
        /// should be used.
        /// </summary>
        /// <param name="endpoint">The specific endpoint you are working with.</param>
        /// <returns>The next x_mte_relay_id from the list of pairIds.</returns>
        private string GetNextPairId(MteRelayEndpoint endpoint)
        {
            if (endpoint.PairIdIdx > endpoint.PairIds!.Count - 1)
            {
                endpoint.PairIdIdx = 0;
            }
            string pairId = endpoint.PairIds[endpoint.PairIdIdx];
            endpoint.PairIdIdx++;
            return pairId;
        }
        #endregion GetNextPairId

        #region private CheckForMTERelayProxy
        /// <summary>
        /// Sends a HEAD message to the MteRelay endpointInformation to ensure it is alive
        /// and if so, it saves the returned MteRelay required headers.
        /// </summary>
        /// <param name="endpoint">The endpoint you wish to use fo check validity.</param>
        /// <returns>True if this endpoint is an MteRelay.</returns>
        /// <exception cref="ApplicationException"></exception>
        private async Task<bool> CheckForMTERelayProxy(MteRelayEndpoint endpoint)
        {
            try
            {
                if (endpoint.mteRelayClient!.BaseAddress is null)
                {
                    throw new ApplicationException($"You must supply a base address for the Mte-Relay endpoint at {endpoint.HttpClientRelayName}.");
                }
                //
                // Create a HEAD request to send to the MteRelay to verify its existence
                // and get back some pairing header values.
                //
                string route = "/api/mte-relay";
                HttpRequestMessage requestMsg = new HttpRequestMessage(HttpMethod.Head, route);
                var httpResponseMsg = await endpoint.mteRelayClient!.SendAsync(requestMsg);

                if (httpResponseMsg is null)
                {
                    throw new ApplicationException($"{route} request failed because the Mte-Relay listening at {endpoint.MteRelayUrl} returned nothing.");
                }
                httpResponseMsg.EnsureSuccessStatusCode();
#pragma warning disable CS8601 // Possible null reference argument.
                foreach (var header in httpResponseMsg.Headers)
                {
                    if (header.Key == Constants.MTE_RELAY_HEADER && header.Value != null)
                    {
                        endpoint.MteRelayClientIdentifier = header.Value.FirstOrDefault();
                    }
                }
#pragma warning restore CS8601 // Possible null reference argument.
                //
                // You should get a valid MteRelayClientIdentifier back from this request.
                //
                if (string.IsNullOrWhiteSpace(endpoint.MteRelayClientIdentifier))
                {
                    throw new ApplicationException($"The server at {endpoint.MteRelayUrl} is not an Mte-Relay server - the {Constants.MTE_RELAY_HEADER} was not returned from the Mte-Relay.");
                }
                return true;
            }
            catch (Exception ex)
            {
                throw new Exception($"{endpoint.MteRelayUrl} is NOT an Mte-Relay endpoint, do NOT use the MteHttpClient.", ex);
            }
        }
        #endregion CheckForMTERelayProxy

        #region private PairWithTheMTERelay
        /// <summary>
        /// The destination server has been identified as a valid Mte-Relay
        /// so pair with it and establish an MKE encoder and decoder.
        /// </summary>
        /// <param name="endpoint">The specific endpoint you wish to pair with.</param>
        /// <returns>TRUE if the pair is successful.</returns>
        private async Task<bool> PairWithTheMTERelay(MteRelayEndpoint endpoint)
        {
            //
            // Create a dictionary to hold KyberHelpers for each requested pair.
            //
            int pairIdsToCreate = endpoint.PairIds!.Count;
            Dictionary<string, IKyberHelper> kyberHelperList = new Dictionary<string, IKyberHelper>(pairIdsToCreate);
            try
            {
                //
                // Prepare the lists of MtePairRequestModels.
                //
                List<MtePairRequestModel> pairValues = new List<MtePairRequestModel>(pairIdsToCreate);
                //
                // Create Kyber objects for each item in the list
                // and make the MtePairRequestModel for that item.
                // This uses the factory method so that we can
                // get different implementations for each endpoint.
                // Finally, add the pairing model to the list.
                //
                for (int i = 0; i < pairIdsToCreate; i++)
                {
                    string pairId = endpoint.PairIds[i];
                    endpoint.XmteRelayHeader.PairId = pairId;
                    kyberHelperList[pairId] = _kyberFactory!.Create();
                    //
                    // Creates the public keys for this pair (encoder and decoder)
                    //
                    pairValues.Add(await MakeSinglePairRequestModel(pairId, kyberHelperList));
                }
                //
                // Ensure that the required header is present
                // Since this is for the pairing request, there are no overrides.
                //
                var resolvedProtectionOptions = ResolveRequestLevelProtectionOverrides(null, _mteRelayOptions);
                endpoint.mteRelayClient!.DefaultRequestHeaders.Add(Constants.MTE_RELAY_HEADER, MakeMteRelayHeaderValue(endpoint, resolvedProtectionOptions));
                //
                // POST the pair request to the Mte Relay endpointInformation server.
                //
                string route = "/api/mte-pair";
                var responseMsg = await endpoint.mteRelayClient!.PostAsync(route, JsonContent.Create<List<MtePairRequestModel>>(pairValues));
                responseMsg.EnsureSuccessStatusCode();
                //
                // Make sure that we received the special Mte Relay headers back from the endpointInformation server.
                //
                CheckResponseHeader(Constants.MTE_RELAY_HEADER, responseMsg);
                //
                // Gather the resulting pairing request list.
                //
                string? json = await responseMsg.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(json))
                {
                    throw new ApplicationException($"The return from pairing with the Mte-Relay for {endpoint.MteRelayUrl} ({route}) is empty.");
                }
                //
                // We receive a list of the "server" side pairing information that correlates with the client side.
                //
                List<MtePairResponseModel>? serverPairRequestList = JsonSerializer.Deserialize<List<MtePairResponseModel>>(json, _jsonOptions);
                if (serverPairRequestList is null || serverPairRequestList.Count == 0)
                {
                    throw new ApplicationException($"The list of pair values from the Mte-Relay at {endpoint.MteRelayUrl} is empty - cannot pair.");
                }
                //
                // Use the Kyber module to create the Entropy needed for all of the MTE Pairs for this endpoint.
                //
                await CreateTheEntropyKeyPairs(pairValues, kyberHelperList, serverPairRequestList);
                return true;
            }
            catch (Exception ex)
            {
                throw new Exception($"Could not pair with the Mte-Relay at {endpoint.mteRelayClient!.BaseAddress}.", ex);
            }
            finally
            {
                foreach (var kh in kyberHelperList)
                {
                    await kh.Value.DisposeAsync();
                }
            }
        }
        #endregion PairWithTheMTERelay        

        #region private EnsureRequestHeader
        /// <summary>
        /// Makes the special x-mte-Relay header value.
        /// </summary>
        /// <param name="endpoint">The specific endpoint whose request headers you are checking.</param>
        /// <param name="resolvedProtectionOptions">The protection options that have been resolved from the request overrides and the configured values.</param>
        /// <param name="isGetRequest">If true, this is a GET request and there is no body to encode.</param>
        /// <param name="hasEncodedHeaders">True if this request has encoded headers. Used to properly set the flags.</param>
        /// <param name="hasContent">True if this request has content. Used to properly set the flags.</param>
        private string MakeMteRelayHeaderValue(MteRelayEndpoint endpoint, MteProtectionOptions resolvedProtectionOptions, bool isGetRequest = false, bool hasEncodedHeaders = true, bool hasContent = true)
        {
            endpoint.XmteRelayHeader.BodyIsEncoded = hasContent;
            endpoint.XmteRelayHeader.UrlIsEncoded = resolvedProtectionOptions.ShouldProtectUrl!.Value;
            switch (resolvedProtectionOptions.HeaderDisposition)
            {
                case RelayHeaderDisposition.EncodeNoHeaders:
                    endpoint.XmteRelayHeader.HeadersAreEncoded = false;
                    break;
                case RelayHeaderDisposition.EncodeAllHeaders:
                    endpoint.XmteRelayHeader.HeadersAreEncoded = true;
                    break;
                case RelayHeaderDisposition.EncodeListOfHeaders:
                    endpoint.XmteRelayHeader.HeadersAreEncoded = true;
                    break;
                default:
                    endpoint.XmteRelayHeader.HeadersAreEncoded = false;
                    break;
            }
            //
            // If there are no encoded headers, be sure to set the proper flag.
            //
            if (!hasEncodedHeaders)
            {
                endpoint.XmteRelayHeader.HeadersAreEncoded = false;
            }
            if (isGetRequest)
            {
                endpoint.XmteRelayHeader.BodyIsEncoded = false; // Do not encode a body for the GET requests.
            }
            endpoint.XmteRelayHeader.ClientId = endpoint.MteRelayClientIdentifier;
            string headerValue = endpoint.XmteRelayHeader.ToString();
            Console.WriteLine($"{Constants.MTE_RELAY_HEADER}: {headerValue}");
            return headerValue;
        }
        #endregion EnsureRequestHeader

        #region private CheckResponseHeader
        /// <summary>
        /// Checks to be sure a specific header is found in the http Response.
        /// </summary>
        /// <param name="headerKey">The header KEY to look for.</param>
        /// <param name="responseMsg">The http Response that should have the requested header.</param>
        /// <exception cref="ApplicationException">If the header is not present.</exception>
        private void CheckResponseHeader(string headerKey, HttpResponseMessage responseMsg)
        {
            if (!responseMsg.Headers.Contains(headerKey))
            {
                throw new ApplicationException($"Could not pair with {responseMsg.RequestMessage!.RequestUri} because the {headerKey} was not found in the relay response.");
            }
        }
        #endregion CheckResponseHeader

        #region private MakeSinglePairRequestModel
        /// <summary>
        /// Creates a single pair request object.
        /// </summary>
        /// <param name="pairId">The specific pair you wish to sync with.</param>
        /// <param name="kyberHelperList">The collection of Kyber wrappers for your pairs.</param>
        /// <returns>MtePairRequestModel to send to the Mte-Relay proxy for this PairId.</returns>
        /// <exception cref="ApplicationException"></exception>
        /// <exception cref="Exception"></exception>
        private async Task<MtePairRequestModel> MakeSinglePairRequestModel(string pairId, Dictionary<string, IKyberHelper> kyberHelperList)
        {
            try
            {
                IKyberHelper? kyberHelper = kyberHelperList[pairId];
                //
                // Create the key pairs for the Encoder and Decoder using Kyber.
                //
                KyberKeys publicKeys = await kyberHelper.GetPublicKeysFromKyberAsync(pairId);

                if (publicKeys == null)
                {
                    throw new ApplicationException("Attempt to get Kyber Public Keys returned nothing");
                }
                if (publicKeys.Drc != 0)
                {
                    throw new ApplicationException($"Attempt to get Decoder Kyber Public Keys returned {publicKeys.Drc}.");
                }
                if (publicKeys.Erc != 0)
                {
                    throw new ApplicationException($"Attempt to get Encoder Kyber Public Keys returned {publicKeys.Erc}.");
                }
                //
                //  Create unique personalization strings for the pairs.
                //
                string encoderPersonalizationString = Guid.NewGuid().ToString();
                string decoderPersonalizationString = Guid.NewGuid().ToString();
                //
                // Create the pairing value objects.
                //
                MtePairRequestModel clientPairRequest = new MtePairRequestModel
                {
                    PairId = pairId,
                    DecoderPersonalizationStr = decoderPersonalizationString,
                    EncoderPersonalizationStr = encoderPersonalizationString,
                    DecoderPublicKey = publicKeys.DecoderPublicKey,
                    EncoderPublicKey = publicKeys.EncoderPublicKey,
                };
                return clientPairRequest;
            }
            catch (Exception ex)
            {
                throw new Exception("Could not create an MKE Pair Request.", ex);
            }
        }
        #endregion MakeSinglePairRequestModel

        #region private CreateTheEntropyKeyPairs
        /// <summary>
        /// Creates the Entropy for all of the Mte Pairs you wish to instance up.
        /// </summary>
        /// <param name="pairValues">The list of Pair Request models.</param>
        /// <param name="kyberHelperList">The list of the encoder Kyber objects.</param>
        /// <param name="serverPairRequestList">The pairing values from the MteRelay server.</param>
        /// <returns>Completed Task</returns>
        /// <exception cref="ApplicationException"></exception>
        /// <exception cref="Exception"></exception>
        private async Task CreateTheEntropyKeyPairs(List<MtePairRequestModel> pairValues, Dictionary<string, IKyberHelper> kyberHelperList, List<MtePairResponseModel>? serverPairRequestList)
        {
            try
            {
                //
                // Create the key pairs for the Encoder and Decoder using Kyber.
                //
                foreach (var clientPairRequest in pairValues)
                {
                    //
                    // Get a kyber Helper from the list that was created
                    // when the public keys were made for the specified x_mte_relay_id
                    //        
                    IKyberHelper? kyberHelper = kyberHelperList[clientPairRequest.PairId!];
                    //
                    // Extract the paired ServerPairRequest from the list returned from the pairing call.
                    //
                    var serverPairRequest = serverPairRequestList!.Where(r => r.PairId == clientPairRequest.PairId).FirstOrDefault();
                    if (serverPairRequest is null)
                    {
                        throw new ApplicationException($"The pair request with Mte-Relay for id {clientPairRequest.PairId} is missing - cannot pair.");
                    }
                    //
                    // Get the entropies from the Kyber object for the encoder and decoder.
                    //                
                    var theEntropiesForThisPair = await kyberHelper.ComputeSharedSecretsFromKyberAsync(clientPairRequest.PairId!, serverPairRequest.DecoderSecret!, serverPairRequest.EncoderSecret!);

                    if (theEntropiesForThisPair.Drc != 0)
                    {
                        throw new ApplicationException($"Could not get a shared secret for the decoder with x_mte_relay_id {clientPairRequest.PairId} rc is: {theEntropiesForThisPair.Drc}");
                    }
                    if (theEntropiesForThisPair.Erc != 0)
                    {
                        throw new ApplicationException($"Could not get a shared secret for the encoder with x_mte_relay_id {clientPairRequest.PairId} rc is: {theEntropiesForThisPair.Erc}");
                    }
                    //
                    // Make the client side MTE Encoder and Decoder
                    //
                    byte[] encoderEntropy = Convert.FromBase64String(theEntropiesForThisPair.EncoderSecret!);
                    byte[] decoderEntropy = Convert.FromBase64String(theEntropiesForThisPair.DecoderSecret!);
                    await MakeSingleClientMKEPair(clientPairRequest, serverPairRequest, encoderEntropy, decoderEntropy);
                    //
                    // Clear out the memory for the next pair of MTEs
                    //
                    Array.Clear(encoderEntropy);
                    Array.Clear(decoderEntropy);
                    theEntropiesForThisPair.Clear();
                    clientPairRequest.Clear();
                    serverPairRequest.Clear();
                }
            }
            catch (Exception ex)
            {
                throw new Exception("Exception in CreateTheEntropyKeyPairs", ex);
            }
        }
        #endregion CreateTheEntropyKeyPairs

        #region private MakeSingleClientMKEPair
        /// <summary>
        /// Creates the actual instances of the MKE Encoder and Decoder for this client'specialHeader x_mte_relay_id.
        /// </summary>
        /// <param name="clientPairRequest">Information we sent to the MTE-Relay.</param>
        /// <param name="serverPairRequest">Information we received from the MTE-Relay.</param>
        /// <param name="decoderEntropy">The entropy that we generated from the key exchange for the decoder.</param>
        /// <param name="encoderEntropy">The entropy that we generated from the key exchange for the encoder.</param>
        /// <exception cref="ApplicationException"></exception>
        private async Task MakeSingleClientMKEPair(MtePairRequestModel clientPairRequest, MtePairResponseModel? serverPairRequest, byte[] decoderEntropy, byte[] encoderEntropy)
        {
            try
            {
                //
                // Grab a Decoder from the collection.
                //
                IJSObjectReference mkeDecoder = await _mteHelper.GetADecoderAsync();

                //
                // Initialize this Decoder
                //                
                MteMagicValues decoderMagicValues = new MteMagicValues
                {
                    Entropy = encoderEntropy,
                    Nonce = serverPairRequest!.EncoderNonce!, // use the server's encoder nonce for the decoder.
                    PersonalizationString = clientPairRequest.DecoderPersonalizationStr,
                };

                MteStatus status = await _mteHelper.InitializeDecoderAsync(mkeDecoder, decoderMagicValues);
                if (status != MteStatus.mte_status_success)
                {
                    throw new ApplicationException($"Could not create an MKE Decoder: {status}");
                }
                //
                // Grab the initial states of the Decoder.
                //
                string decoderState = await _mteHelper.RetrieveDecoderStateAsync(mkeDecoder);

                //
                // Grab an Encoder from the collection.
                //
                IJSObjectReference mkeEncoder = await _mteHelper.GetAnEncoderAsync();

                //
                // InitializeAsync this Encoder.
                //                
                MteMagicValues encoderMagicValues = new MteMagicValues
                {
                    Entropy = decoderEntropy,
                    Nonce = serverPairRequest!.DecoderNonce!, // use the server's decoder nonce for the encoder.
                    PersonalizationString = clientPairRequest.EncoderPersonalizationStr,
                };

                status = await _mteHelper.InitializeEncoderAsync(mkeEncoder, encoderMagicValues);
                if (status != MteStatus.mte_status_success)
                {
                    throw new ApplicationException($"Could not create an MKE Encoder: {status}");
                }
                //
                // Grab the initial states of the Encoder.
                //                
                string encoderState = await _mteHelper.RetrieveEncoderStateAsync(mkeEncoder);
                //
                // Store the initial states for this specific pairing.
                //
                MteState theEncoderState = new MteState { StateType = MteState.MteType.Encoder, B64State = encoderState, PairId = clientPairRequest.PairId };
                PutEncoderStateintoCache(clientPairRequest.PairId!, theEncoderState);
                MteState theDecoderState = new MteState { StateType = MteState.MteType.Decoder, B64State = decoderState, PairId = clientPairRequest.PairId };
                PutDecoderStateintoCache(clientPairRequest.PairId!, theDecoderState);
            }
            catch (Exception ex)
            {
                throw new Exception($"Exception trying to create MKE pairs for {clientPairRequest.PairId}.", ex);
            }
        }
        #endregion MakeSingleClientMKEPair

        #region private ReplacePairedEndpoint
        /// <summary>
        /// Replaces a failed MteRelay endpoint
        /// due to some kind of failure after trying Constants.MAX_PROXY_RETRY_ATTEMPTS.
        /// </summary>
        /// <param name="pairIds">List of valid pairIds.</param>
        /// <param name="pairId">The specific x_mte_relay_id to replace.</param>        
        private async Task ReplacePairedEndpoint(MteRelayEndpoint endpoint)
        {
            try
            {
                //
                // Remove the states associated with the 'bad' endpoint.
                //
                foreach (var pairId in endpoint.PairIds!)
                {
                    var badEncoderState = GetEncoderStateFromCache(pairId);

                    if (badEncoderState != null)
                    {
                        string key = $"E-{pairId}";
                        _mteStateDictionary.Remove<string, string>(key, out _);
                    }

                    var badDecoderState = GetDecoderStateFromCache(pairId);
                    if (badDecoderState != null)
                    {
                        string key = $"D-{pairId}";
                        _mteStateDictionary.Remove<string, string>(key, out _);
                    }
                }

                //
                // Reinitialize this endpoint since MteRelay indicated an error
                //
                await InitializeAsync(endpoint, true);
            }
            catch (Exception ex)
            {
                //
                // If this fails the endpoint for this API is no longer responding.
                //
                _logger.LogError(ex, $"Could not re-initialize your Mte-Relay at {endpoint.MteRelayUrl}, perhaps it has stopped responding.");
            }
        }
        #endregion

        #region private CreateDecodedHttpResponseMessage
        /// <summary>
        /// Checks the relayResponse status code, and if OK, returns a decoded relayResponse object.
        /// </summary>
        /// <param name="pairId">The PairId you are decoding a response for.</param>
        /// <param name="originalContent">The original content - needed for the retry logic.</param>
        /// <param name="response">The relayResponse from the MTE-Relay</param>
        /// <returns>Response Message with the clearPayload decoded.</returns>
        /// <exception cref="MteRelayException">Thrown if a retry is needed - has all the information for a retry.</exception>
        private async Task<HttpResponseMessage> CreateDecodedHttpResponseMessage(string pairId, HttpResponseMessage response, HttpContent? originalContent = null)
        {
            int intResponseCode = (int)response.StatusCode;
            //
            // Scaffold to allow you to test retry logic.
            //
            if (testRetryAttempts == 1)
            {
                intResponseCode = 560;
            }
            //
            // If this was a succesful Response, return a new ResponseMessage
            // with a decoded payload content and the proper content-type header.
            //
            if (intResponseCode >= 200 && intResponseCode <= 299)
            {
                HttpResponseMessage responseMessage = await MakeClearResponseMessage(response);
                return responseMessage;
            }
            //
            // MteRelay relayResponse codes for failures at the endpointInformation server
            // are in the 559-570 range, so if we received that throw an exception
            // which will invoke the retry logic.
            //
            else if (intResponseCode >= 559 && intResponseCode <= 570)
            {
                //
                // Set the actual relayResponse to InternalServerError
                // and set the Reason phrase to what was returned from the MteRelay endpointInformation.
                //
                response.StatusCode = HttpStatusCode.InternalServerError;
                response.ReasonPhrase = $"{intResponseCode} - {Constants.ParseRelayRC(intResponseCode)}";
                MteRelayException mrex = new MteRelayException($"Mte-Relay had an error - the status code is {intResponseCode} Message: {response.ReasonPhrase}.")
                {
                    HttpReasonPhrase = response.ReasonPhrase,
                    HttpStatusCode = intResponseCode,
                    PairId = pairId,
                    OriginalContent = originalContent,
                };
                _logger.LogError(mrex, $"Failed attempt to send to the MteRelay for {mrex.PairId}. This will be retried if possible.");
                throw mrex;
            }
            //
            // If any other status is found, just return the relayResponse from Mte-Relay
            // because it was passed through from the API.
            //
            else
            {
                return response;
            }
        }
        #endregion CreateDecodedHttpResponseMessage

        #region private MakeHttpRelayRequest
        /// <summary>
        /// Creates an HttpRequestMessage for the specified route and method.
        /// </summary>
        /// <param name="content">The optional (not used for GET) content to include in the Request Message.</param>
        /// <param name="route">The specific route within the connection base address.</param>
        /// <param name="method">The Http Method to send.</param>
        /// <param name="encodedHeaders">An Mte encoded string of the content-type and other custom headers.</param>
        /// <param name="clearHeaders">A collection of custom headers that are NOT encoded.</param>
        /// <returns>HttpRequestMessage ready to send to the MteRelay endpoint server.</returns>
        private HttpRequestMessage MakeHttpRelayRequest(HttpContent? content, string route, HttpMethod method, string encodedHeaders, Dictionary<string, string> clearHeaders)
        {
            try
            {
                HttpRequestMessage request = new HttpRequestMessage(method, route);
                //
                // Add the content if we have any.
                //
                if (content is not null)
                {
                    request.Content = content;
                }
                //
                // Add any non-encoded headers.
                //
                if (clearHeaders is not null && clearHeaders.Count > 0)
                {
                    foreach (var key in clearHeaders.Keys)
                    {
                        request.Headers.Add(key, clearHeaders[key]);
                    }
                }
                //
                // Add the special encoded header (this includes the content-type).
                //
                if (!string.IsNullOrWhiteSpace(encodedHeaders))
                {
                    request.Headers.Add(Constants.MTE_RELAY_ENCODED_HEADER, encodedHeaders);
                }
                return request;
            }
            catch (Exception ex)
            {
                throw new Exception($"Could not create Http {method} Request to {route}", ex);
            }
        }
        #endregion MakeHttpRelayRequest

        #region private MakeEncodedRoute
        /// <summary>
        /// If the Url is to be protected, this encodes the route portion of it.
        /// </summary>
        /// <param name="pairId">Ths specific pair id.</param>
        /// <param name="route">The route (the leading slash is trimmed off).</param>
        /// <returns>The encoded route to your eventual destinaiton API.</returns>
        /// <exception cref="ApplicationException"></exception>
        /// <exception cref="Exception"></exception>
        private async Task<string> MakeEncodedRoute(string pairId, string route)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(route))
                {
                    route = route.TrimStart('/');
                    //                
                    // Retrieve the Encoder for the specific pair.
                    //                
                    IJSObjectReference mkeEncoder = await _mteHelper.GetAnEncoderAsync();
                    if (mkeEncoder is not null)
                    {
                        await HydrateTheEncoder(pairId!, mkeEncoder);
                    }
                    else
                    {
                        throw new ApplicationException($"No MteEncoder is found for PairId: {pairId}");
                    }
                    string encoded = await _mteHelper.EncodeToStringAsync(mkeEncoder, route);
                    //
                    // Protect the state and put the encoder back in the bag.
                    //
                    await GetCurrentEncoderStateAndStoreInCache(pairId!, mkeEncoder);
                    _mteHelper.PutTheEncoder(mkeEncoder);
                    return HttpUtility.UrlEncode(encoded);
                }
                return string.Empty;
            }
            catch (Exception ex)
            {
                throw new Exception($"Could not encode the requested route: {route}", ex);
            }
        }
        #endregion

        #region private MakeEncodedPayloadHeader
        /// <summary>
        /// Check to see if the custom headers includes a content type, and if not add one.
        /// This also ensures that a Constants.RELAY_PAIR_ID_HEADER header is included so that
        /// the paired MkeEncoders and MkeDecoders work properly in sync.
        /// </summary>
        /// <param name="endpoint">The specific endpoint you are building a header collection for.</param>
        /// <param name="method">The HttpMethod you are building a header collection for.</param>
        /// <param name="headers">Dictionary of custom headers.</param>
        /// <param name="defaultContentType">Mime type to add if required.</param>
        /// <param name="protectionOptions">Optional override options to manage protection of URL and Headers.</param>
        private async Task<(Dictionary<string, string>? clearHeaders, string? encodedHeaders)> MakeEncodedPayloadHeader(MteRelayEndpoint endpoint, HttpMethod method, Dictionary<string, string>? headers, string defaultContentType, MteProtectionOptions protectionOptions)
        {
            try
            {
                if (headers is null)
                {
                    headers = new Dictionary<string, string>();
                }
                //
                // Add or replace any DefaultRequestHeaders for this endpoint to the headers collection.
                //
                if (endpoint.MteDefaultRequestHeaders is not null && endpoint.MteDefaultRequestHeaders.Count > 0)
                {
                    foreach (var key in endpoint.MteDefaultRequestHeaders.Keys)
                    {
                        if (headers.ContainsKey(key))
                        {
                            headers[key] = endpoint.MteDefaultRequestHeaders[key];
                        }
                        else
                        {
                            headers.Add(key, endpoint.MteDefaultRequestHeaders[key]);
                        }
                    }
                }
                //
                // If we designated an Authentication Header, add or replace it.
                //
                if (endpoint.AuthenticationHeader is not null && !string.IsNullOrEmpty(endpoint.AuthenticationHeader.Value))
                {
                    if (headers.ContainsKey(Constants.AUTHORIZATION_HEADER))
                    {
                        headers[Constants.AUTHORIZATION_HEADER] = $"{endpoint.AuthenticationHeader.Scheme} {endpoint.AuthenticationHeader.Value}";
                    }
                    else
                    {
                        headers.Add(Constants.AUTHORIZATION_HEADER, $"{endpoint.AuthenticationHeader.Scheme} {endpoint.AuthenticationHeader.Value}");
                    }
                }
                //
                // Create some dictionaries to contain the requested headers.
                //
                Dictionary<string, string>? clearHeaders = new Dictionary<string, string>();
                Dictionary<string, string>? headersToEncode = new Dictionary<string, string>();
                //
                // If we have a "credentials" header, it must stay in the clear
                //
                string? credentialKey = headers.Keys.Where(k => k.Equals("credentials", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
                if (credentialKey != null)
                {
                    clearHeaders.Add(credentialKey, headers[credentialKey]);
                }
                if (defaultContentType != Constants.STR_CONTENT_NO_CONTENT)
                {
                    //
                    // Check to see if a content-type header was sent from the application.
                    //
                    string? contentTypeKey = headers.Keys.Where(k => k.Equals(Constants.STR_CONTENT_TYPE_HEADER, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
                    //
                    // Add the content-type header to the headers to encode if this is not a GET request.
                    //
                    if (method != HttpMethod.Get)
                    {
                        if (!string.IsNullOrWhiteSpace(contentTypeKey))
                        {
                            headersToEncode.Add(Constants.STR_CONTENT_TYPE_HEADER, headers[contentTypeKey]);
                        }
                        else
                        {
                            headersToEncode.Add(Constants.STR_CONTENT_TYPE_HEADER, defaultContentType);
                        }
                    }
                }
                //
                // If we wish to encode all headers, add them to the list of headers to encode (the content-type is always added).
                //
                if (protectionOptions.HeaderDisposition == RelayHeaderDisposition.EncodeAllHeaders)
                {
                    foreach (var header in headers)
                    {
                        if (!header.Key.Equals(Constants.STR_CONTENT_TYPE_HEADER, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!clearHeaders.ContainsKey(header.Key))
                            {
                                headersToEncode.Add(header.Key, header.Value);
                            }
                        }
                    }
                }
                //
                // If we only wish to encode certain headers, add them to the list of headers to encode
                // and add any other headers to the clear headers list.
                //
                else if (protectionOptions.HeaderDisposition == RelayHeaderDisposition.EncodeListOfHeaders)
                {
                    foreach (string key in headers.Keys)
                    {
                        if (!key.Equals(Constants.STR_CONTENT_TYPE_HEADER, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!clearHeaders.ContainsKey(key))
                            {
                                if (protectionOptions.HeadersToEncode.Contains(key))
                                {
                                    headersToEncode.Add(key, headers[key]);
                                }
                                else
                                {
                                    clearHeaders.Add(key, headers[key]);
                                }
                            }
                        }
                    }
                }
                //
                // Otherwise if no headers need to be encoded, just skip them
                // except for the ContentType header - it is always encoded.
                //
                else if (protectionOptions.HeaderDisposition == RelayHeaderDisposition.EncodeNoHeaders)
                {
                    foreach (string key in headers.Keys)
                    {
                        if (!key.Equals(Constants.STR_CONTENT_TYPE_HEADER, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!clearHeaders.ContainsKey(key))
                            {
                                clearHeaders.Add(key, headers[key]);
                            }
                        }
                    }
                }
                //
                // If we have some to encode (we should always have content-type unless it is a GET request),
                // then encode them to include in the encoded headers header.
                //
                string encodedHeaders = string.Empty;
                if (headersToEncode.Count > 0)
                {
                    //                
                    // Retrieve the Encoder for the specific pair.
                    //                
                    IJSObjectReference mkeEncoder = await _mteHelper.GetAnEncoderAsync();
                    if (mkeEncoder is not null)
                    {
                        await HydrateTheEncoder(endpoint.XmteRelayHeader.PairId!, mkeEncoder);
                    }
                    else
                    {
                        throw new ApplicationException($"No MteEncoder is found for MteRelayClientIdentifier: {endpoint.MteRelayClientIdentifier}");
                    }
                    //
                    // Serialize the list of headers to encode and use MTE to encode them.
                    //
                    encodedHeaders = await _mteHelper.EncodeToStringAsync(mkeEncoder, JsonSerializer.Serialize(headersToEncode));
                    if (string.IsNullOrWhiteSpace(encodedHeaders))
                    {
                        throw new ApplicationException($"Could not encode the custom headers for the Mte-Relay.");
                    }
                    //
                    // Protect the state and put the encoder back in the bag.
                    //
                    await GetCurrentEncoderStateAndStoreInCache(endpoint.XmteRelayHeader.PairId!, mkeEncoder);
                    _mteHelper.PutTheEncoder(mkeEncoder);
                    return (clearHeaders, encodedHeaders);
                }
                else
                {
                    return (clearHeaders, string.Empty);
                }
            }
            catch (Exception ex)
            {
                throw new Exception("Could not create the Encoded payload header to send to the endpoint server.", ex);
            }
        }
        #endregion MakeEncodedPayloadHeader 

        #region private MakeEncodedContent (overloaded for string and byte[])
        /// <summary>
        /// Mte encodes an incoming string and returns
        /// a ByteArrayContent of the encoded data.
        /// </summary>
        /// <param name="data">The string content to encode.</param>
        /// <param name="pairId">The pair you are making content for.</param>
        /// <returns>ByteArrayContent with the proper content header and the encoded data.</returns>
        /// <exception cref="ApplicationException"></exception>
        private async Task<HttpContent> MakeEncodedContent(string data, string pairId)
        {
            try
            {
                //
                // Get the paired encoder and encode the incoming data.
                //
                IJSObjectReference mkeEncoder = await _mteHelper.GetAnEncoderAsync();
                if (mkeEncoder is not null)
                {
                    await HydrateTheEncoder(pairId, mkeEncoder);
                }
                else
                {
                    throw new ApplicationException($"No MteEncoder is found for x_mte_relay_id: {pairId}");
                }

                //
                // The MteRelay requires an encoded byte array of the byte representation 
                // of a string. So if Json is coming in (or any other string), you must
                // convert it to bytes prior to sending it.
                //
                byte[]? encoded = await _mteHelper.EncodeAsync(mkeEncoder, Encoding.UTF8.GetBytes(data));
                if (encoded is null)
                {
                    throw new ApplicationException($"Could not encode the string payload.");
                }
                //
                // Protect the state and put the encoder back in the bag.
                //
                await GetCurrentEncoderStateAndStoreInCache(pairId, mkeEncoder);
                _mteHelper.PutTheEncoder(mkeEncoder);
                //
                // Create a byte array content of the encoded payload.
                //
                var body = new ByteArrayContent(encoded);
                body.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
                return body;
            }
            catch (Exception ex)
            {
                throw new Exception($"Could not make an MKE encoded request from the clear string.", ex);
            }
        }

        public async Task<HttpContent> MakeEncodedContent(byte[] data, string pairId)
        {
            try
            {
                //
                // Get the paired encoder and encode the incoming data.
                //
                IJSObjectReference mkeEncoder = await _mteHelper.GetAnEncoderAsync();
                if (mkeEncoder is not null)
                {
                    await HydrateTheEncoder(pairId, mkeEncoder);
                }
                else
                {
                    throw new ApplicationException($"No MteEncoder is found for x_mte_relay_id: {pairId}");
                }

                byte[]? encoded = await _mteHelper.EncodeAsync(mkeEncoder, data);

                if (encoded is null)
                {
                    throw new ApplicationException($"Could not encode the byte[] clearPayload.");
                }
                //
                // Protect the state and put the encoder back in the bag.
                //
                await GetCurrentEncoderStateAndStoreInCache(pairId, mkeEncoder);
                _mteHelper.PutTheEncoder(mkeEncoder);

                var encodedContent = new ByteArrayContent(encoded);
                encodedContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
                return encodedContent;
            }
            catch (Exception ex)
            {
                throw new Exception($"Could not make an MKE encoded request from the clear byte array.", ex);
            }
        }
        #endregion MakeEncodedContent

        #region private MakeClearResponseMessage
        /// <summary>
        /// The relayResponse from the MTE-Relay is always an encoded byte array,
        /// so create a new HttpResponse with the decoded bytes.
        /// </summary>
        /// <param name="response">The encoded relayResponse from the MteRelay API server.</param>
        /// <returns>A new HttpResponse with the decoded content and the proper content-type header.</returns>
        private async Task<HttpResponseMessage> MakeClearResponseMessage(HttpResponseMessage response)
        {
            // TODO: Handle cookies from the server...
            // https://learn.microsoft.com/en-us/aspnet/web-api/overview/advanced/http-cookies
            try
            {
                //
                // Get the encoded headerName so we can determine the content type.
                //
                KeyValuePair<string, IEnumerable<string>>? encodedMteRelayHeaders = response.Headers
                    .Where(h => h.Key == Constants.MTE_RELAY_ENCODED_HEADER)
                    .FirstOrDefault();
                //
                // Make sure that we received a EncodedHeaders header.
                //
                if (encodedMteRelayHeaders is null)
                {
                    throw new ApplicationException($"Mte-Relay must return a {Constants.MTE_RELAY_ENCODED_HEADER} header and none was returned.");
                }
                if (!encodedMteRelayHeaders.HasValue)
                {
                    throw new ApplicationException($"Mte-Relay return a {Constants.MTE_RELAY_ENCODED_HEADER} header but it was empty.");
                }
                //
                // The relayResponse must include a header value for this pair id
                //
                var pairIdHeader = response.Headers.Where(h => h.Key == Constants.MTE_RELAY_HEADER).FirstOrDefault();
                if (pairIdHeader.Value is null)
                {
                    throw new ApplicationException($"The relayResponse requires a {Constants.MTE_RELAY_HEADER} header and none was returned.");
                }
                //
                // Find out which pair we are working with.
                //
                X_MteRelay_Header resultHeader = new X_MteRelay_Header(pairIdHeader.Value.FirstOrDefault()!);
                string pairId = resultHeader.PairId!;
                //
                // Since the encoded relay header collection is protected with MTE, decode it.
                //
                string encodedRelayHeaders = encodedMteRelayHeaders.Value.Value.First();
                if (string.IsNullOrWhiteSpace(encodedRelayHeaders) || !resultHeader.HeadersAreEncoded)
                {
                    var endpoint = GetEndpointFromId(pairId!);
                    throw new ApplicationException($"Http relayResponse from Mte-Relay at {endpoint!.MteRelayUrl} did not return an encoded Header for x_mte_relay_id {pairId}.");
                }
                var decodedRelayHeaders = await MteDecodeCustomHeaderCollection(encodedRelayHeaders, pairId!);
                //
                // Get the content type that the original API set for this relayResponse.
                //
                var contentTypeKey = decodedRelayHeaders!.Keys.Where(k => k.Equals(Constants.STR_CONTENT_TYPE_HEADER, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
                if (string.IsNullOrWhiteSpace(contentTypeKey))
                {
                    throw new ApplicationException($"Mte-Relay must return a 'content-type' header in its {Constants.MTE_RELAY_ENCODED_HEADER} header collection - it did not.");
                }
                string contentType = decodedRelayHeaders[contentTypeKey];
                if (string.IsNullOrEmpty(contentType))
                {
                    throw new ApplicationException($"Mte-Relay must return a valid 'content-type' header in its {Constants.MTE_RELAY_ENCODED_HEADER} header collection, but it was blank.");
                }

                if (response.Content is not null && response.StatusCode != HttpStatusCode.NoContent)
                {
                    HttpContent? content = null;
                    //
                    // Get the encoded byte array from the response.Content and decode it.
                    //
                    byte[] encoded = await response.Content.ReadAsByteArrayAsync();
                    byte[] decoded = await MTEDecodeToByteArray(encoded, pairId!);
                    //
                    // If the returned payload is a "text" type, convert the
                    // decoded bytes to a string and create a string content
                    // return object.
                    //
                    if (Constants.CheckForText(contentType))
                    {
                        string theValue = Encoding.UTF8.GetString(decoded);
                        content = new StringContent(theValue);
                    }
                    //
                    // Otherwise, create a byte array content to return.
                    //
                    else
                    {
                        content = new ByteArrayContent(decoded, 0, decoded.Length);
                    }
                    //
                    // Set the new content to the content type returned from the relay rather than the default.
                    //
                    content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
                    //
                    // Set the content headers from the original relayResponse into the new content object we created
                    //
                    foreach (var h in response.Content.Headers)
                    {
                        //
                        // Skip content length and content type
                        // they get set when we create the new content.
                        //
                        if (h.Key.ToLower() == Constants.STR_CONTENT_TYPE_HEADER || h.Key.ToLower() == Constants.STR_CONTENT_LENGTH_HEADER)
                        {
                            continue;
                        }
                        content.Headers.Add(h.Key, h.Value);
                    }
                    //
                    // Replace the response content with the decoded content
                    //
                    response.Content = content;
                }
                //
                // Since we decoded some headers, add them back into the response.
                //
                foreach (var header in decodedRelayHeaders)
                {
                    if (!header.Key.Equals(Constants.STR_CONTENT_TYPE_HEADER, StringComparison.OrdinalIgnoreCase))
                    {
                        response.Headers.Add(header.Key, header.Value);
                    }
                }
                //
                // If we have an encoded header (we should always), remove it before
                // sending the relayResponse back to the client
                //
                if (response.Headers.Contains(Constants.MTE_RELAY_ENCODED_HEADER))
                {
                    response.Headers.Remove(Constants.MTE_RELAY_ENCODED_HEADER);
                }
                return response;
            }
            catch (Exception ex)
            {
                throw new Exception("Could not transform the encoded relayResponse to a clear relayResponse.", ex);
            }
        }

        #endregion MakeClearResponseMessage

        #region private MTEDecodeCustomHeaderCollection
        /// <summary>
        /// Several headers may come back from the Proxy (MTE-Relay) server
        /// among them is the Content-Type header.  These are all MTE encoded
        /// in a header with the key of Constants.MTE_RELAY_ENCODED_HEADER
        /// as a collection.  This method decodes them and it must be done
        /// first before decoding the actual clearPayload.
        /// </summary>
        /// <param name="encoded">A B-64 string of all of the custom headers
        /// especially the content-type header.</param>
        /// <param name="pairId">This is the x_mte_relay_id to decode with.</param>
        /// <returns>The collection of decoded headers.</returns>
        private async Task<Dictionary<string, string>?> MteDecodeCustomHeaderCollection(string encoded, string pairId)
        {
            try
            {
                IJSObjectReference mkeDecoder = await _mteHelper.GetADecoderAsync();
                if (mkeDecoder != null)
                {
                    await HydrateTheDecoder(pairId, mkeDecoder);
                }
                else
                {
                    throw new ApplicationException($"No Decoder found for x_mte_relay_id {pairId}");
                }
                //
                // Decode the encoded header into a Dictionary.
                // TODO: Figure out why it is getting token does not exist on the decode.
                // TODO: Clean up stuff since magically, encoding seems to be working.......
                //
                string clearHeaders = await _mteHelper.DecodeToStringAsync(mkeDecoder, encoded);
                await GetCurrentDecoderStateAndStoreInCache(pairId, mkeDecoder);
                _mteHelper.PutTheDecoderAsync(mkeDecoder);

                if (string.IsNullOrWhiteSpace(clearHeaders))
                {
                    throw new ApplicationException($"Could not MTE decode the {Constants.MTE_RELAY_ENCODED_HEADER} headerName.");
                }
                return JsonSerializer.Deserialize<Dictionary<string, string>>(clearHeaders);
            }
            catch (Exception ex)
            {
                throw new Exception("Exception decoding the encoded Mte-Relay header collection.", ex);
            }
        }
        #endregion MTEDecodeCustomHeaderCollection

        #region private MTEDecodeToByteArray
        /// <summary>
        /// The return from the MTE-Relay is always a byte array
        /// so decode it so that it can be returned to tha app.
        /// </summary>
        /// <param name="encoded">The encoded byte array from the MTE-Relay.</param>
        /// <param name="pairId">The specific x_mte_relay_id of the MteState to hydrate with.</param>
        /// <returns>A Decoded bypte array.</returns>
        /// <exception cref="ApplicationException">Any non success status from MKE.</exception>
        private async Task<byte[]> MTEDecodeToByteArray(byte[] encoded, string pairId)
        {
            IJSObjectReference mkeDecoder = await _mteHelper.GetADecoderAsync();
            if (mkeDecoder != null)
            {
                await HydrateTheDecoder(pairId, mkeDecoder);
            }
            else
            {
                throw new ApplicationException($"No Decoder found for x_mte_relay_id {pairId}");
            }
            byte[] decoded = await _mteHelper.DecodeToByteArrayAsync(mkeDecoder, encoded);
            await GetCurrentDecoderStateAndStoreInCache(pairId, mkeDecoder);
            _mteHelper.PutTheDecoderAsync(mkeDecoder);
            if (decoded is null)
            {
                throw new ApplicationException($"Error decoding the mteRelay returned payload.");
            }
            return decoded;
        }

        #endregion MTEDecodeToByteArray

        #region private HydrateTheEncoder
        /// <summary>
        /// Gets the state from the cache for this encoder and hydrates the encoder with it.
        /// </summary>
        /// <param name="pairId">The pairid of the encoder state.</param>
        /// <param name="mkeEncoder">The encoder</param>
        /// <returns>Completed task.</returns>
        /// <exception cref="ApplicationException"></exception>
        private async Task HydrateTheEncoder(string pairId, IJSObjectReference mkeEncoder)
        {
            try
            {
                var states = GetEncoderStateFromCache(pairId);
                if (states is null)
                {
                    throw new ApplicationException($"No MteEncoder states found for x_mte_relay_id: {pairId}");
                }

                if (string.IsNullOrWhiteSpace(states.B64State))
                {
                    throw new ApplicationException($"MteEncoder states is empty for x_mte_relay_id: {pairId}");
                }
                await _mteHelper.RestoreEncoderStateAsync(mkeEncoder, states.B64State);
            }
            catch (Exception ex)
            {
                throw new ApplicationException($"Exception Hydrating the Encoder for pair {pairId}", ex);
            }
        }
        #endregion

        #region private HydrateTheDecoder
        /// <summary>
        /// Gets the state from the cache for this decoder and hydrates the decoder with it.
        /// </summary>
        /// <param name="pairId">The pairid of the decoder state.</param>
        /// <param name="mkeDecoder">The decoder</param>
        /// <returns>Completed task.</returns>
        /// <exception cref="ApplicationException"></exception>
        private async Task HydrateTheDecoder(string pairId, IJSObjectReference mkeDecoder)
        {
            try
            {
                MteState states = GetDecoderStateFromCache(pairId);
                if (string.IsNullOrEmpty(states.B64State))
                {
                    throw new ApplicationException($"MteDecoder states is empty for x_mte_relay_id: {pairId}");
                }
                await _mteHelper.RestoreDecoderStateAsync(mkeDecoder, states.B64State);
            }
            catch (Exception ex)
            {
                throw new ApplicationException($"Exception Hydrating the Decoder for pair {pairId}", ex);
            }
        }
        #endregion

        #region private GetDecoderStateFromCache
        /// <summary>
        ///  Retrieves the current state from the cache for the designated x_mte_relay_id.
        /// </summary>
        /// <param name="pairId">The pairid of the decoder you wish to retrieve.</param>
        /// <returns>The retrieved state.</returns>
        /// <exception cref="ApplicationException"></exception>
        /// <exception cref="Exception"></exception>
        private MteState GetDecoderStateFromCache(string pairId)
        {
            try
            {
                string? statesJson = _mteStateDictionary.GetValueOrDefault($"D-{pairId}");
                if (string.IsNullOrWhiteSpace(statesJson))
                {
                    throw new ApplicationException($"No MteState found in Cache for x_mte_relay_id {pairId}.");
                }
#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type.
                MteState states = JsonSerializer.Deserialize<MteState>(statesJson);
#pragma warning restore CS8600 // Converting null literal or possible null value to non-nullable type.
                if (states is null)
                {
                    throw new ApplicationException($"No MteDecoder states found for x_mte_relay_id: {pairId}");
                }
                return states;
            }
            catch (Exception ex)
            {
                throw new Exception("Exception getting Decoder State from Cache", ex);
            }
        }
        #endregion

        #region private GetEncoderStateFromCache
        /// <summary>
        ///  Retrieves the current state from the Cache for the designated x_mte_relay_id.
        /// </summary>
        /// <param name="pairId">The pairid of the encoder you wish to retrieve.</param>
        /// <returns>The retrieved state.</returns>
        /// <exception cref="ApplicationException"></exception>
        /// <exception cref="Exception"></exception>
        private MteState GetEncoderStateFromCache(string pairId)
        {
            try
            {
                //string? statesJson = await _mteHelper.RetrieveFromSDRAsync($"E-{x_mte_relay_id}");
                string? statesJson = _mteStateDictionary.GetValueOrDefault($"E-{pairId}");
                if (string.IsNullOrWhiteSpace(statesJson))
                {
                    throw new ApplicationException($"No MteState found in Cache for x_mte_relay_id {pairId}.");
                }
#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type.
                MteState states = JsonSerializer.Deserialize<MteState>(statesJson);
#pragma warning restore CS8600 // Converting null literal or possible null value to non-nullable type.
                if (states is null)
                {
                    throw new ApplicationException($"No MteEncoder states found for x_mte_relay_id: {pairId}");
                }
                return states;
            }
            catch (Exception ex)
            {
                throw new Exception("Exception getting Encoder State from Cache", ex);
            }
        }
        #endregion

        #region private PutDecoderStateintoCache
        /// <summary>
        /// Places the current state of the MTE into the Cache.
        /// </summary>
        /// <param name="pairid">The x_mte_relay_id associated with the decoder.</param>
        /// <param name="states">The current object with the state.</param>
        /// <returns>Completed Task.</returns>
        /// <exception cref="Exception"></exception>
        private void PutDecoderStateintoCache(string pairid, MteState states)
        {
            try
            {
                string key = $"D-{pairid}";
                string json = JsonSerializer.Serialize(states);
                _mteStateDictionary[key] = json;
            }
            catch (Exception ex)
            {
                throw new Exception("Exception putting Decoder State into Cache", ex);
            }
        }
        #endregion

        #region private PutEncoderStateintoCache
        /// <summary>
        /// Places the current state of the MTE into the Cache.
        /// </summary>
        /// <param name="pairid">The x_mte_relay_id associated with the encoder.</param>
        /// <param name="states">The current object with the state.</param>
        /// <returns>Completed Task.</returns>
        /// <exception cref="Exception"></exception>
        private void PutEncoderStateintoCache(string pairid, MteState states)
        {
            try
            {
                string key = $"E-{pairid}";
                string json = JsonSerializer.Serialize(states);
                _mteStateDictionary[key] = json;
            }
            catch (Exception ex)
            {
                throw new Exception("Exception putting Encoder State into Cache", ex);
            }
        }
        #endregion

        #region private GetCurrentEncoderStateAndStoreInCache
        /// <summary>
        /// Gets the Encoder state from an MTE and stores it in the Cache.
        /// </summary>
        /// <param name="pairId">The specific pairid - used as the "key" to storage</param>
        /// <param name="mkeEncoder">The specific Encoder that has the state to save.</param>
        /// <returns>Completed Task</returns>
        /// <exception cref="ApplicationException"></exception>
        private async Task GetCurrentEncoderStateAndStoreInCache(string pairId, IJSObjectReference mkeEncoder)
        {
            var states = GetEncoderStateFromCache(pairId);
            if (states is null)
            {
                throw new ApplicationException($"No MteEncoder states found for x_mte_relay_id: {pairId}");
            }
            states.B64State = await _mteHelper.RetrieveEncoderStateAsync(mkeEncoder);
            PutEncoderStateintoCache(pairId, states);
        }
        #endregion

        #region private GetCurrentDecoderStateAndStoreInCache        
        /// <summary>
        /// Gets the Decoder state from an MTE and stores it in the Cache.
        /// </summary>
        /// <param name="pairId">The specific pairid - used as the "key" to storage</param>
        /// <param name="mkeEncoder">The specific Decoder that has the state to save.</param>
        /// <returns>Completed Task</returns>
        /// <exception cref="ApplicationException"></exception>
        private async Task GetCurrentDecoderStateAndStoreInCache(string pairId, IJSObjectReference mkeDecoder)
        {
            var states = GetDecoderStateFromCache(pairId);
            if (states is null)
            {
                throw new ApplicationException($"No MteDecoder states found for x_mte_relay_id: {pairId}");
            }
            states.B64State = await _mteHelper.RetrieveDecoderStateAsync(mkeDecoder);
            PutDecoderStateintoCache(pairId, states);
        }
        #endregion

        #region static MakeByteArrayString
        /// <summary>
        /// Helper (static) to log out a byte array - left in this class for debugging.
        /// </summary>
        /// <param name="bytes">An array of bytes to "stringify".</param>
        /// <returns>String of the byte values.</returns>
        public static string MakeByteArrayString(byte[] bytes)
        {
            StringBuilder sb = new StringBuilder();
            foreach (var b in bytes)
            {
                sb.Append($"{b.ToString()}, ");
            }
            return sb.ToString();
        }
        #endregion
    }
}