﻿using Haley.Enums;
using Haley.Utils;
using System;
using Haley.Abstractions;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Diagnostics;
using System.Collections.Concurrent;
using Haley.Models;
using Trs =System.Timers;
using System.Web;

namespace Haley.Utils
{
    /// <summary>
    /// A simple straightforward HTTP helper client.
    /// </summary>
    public sealed class MicroClient :IClient
    {
        public HttpClient BaseClient { get; }
        public string Id { get; }
        #region Attributes
        //private object requestSemaphore = new object(); //DONOT USE LOCK OR MONITOR. IT DOESN'T WORK AS EXPECTED WITH ASYNC AWAIT. USE SEMAPHORESLIM
        private SemaphoreSlim requestSemaphore = new SemaphoreSlim(1,1); //Only 1 request to be granted (for this client).
        private Uri _base_uri;
        private string request_token;
        private ConcurrentDictionary<string, IEnumerable<string>> _requestHeaders = new ConcurrentDictionary<string, IEnumerable<string>>();
        private CancellationToken cancellation_token = default(CancellationToken);
        private bool add_cancellation_token = false;
        Trs.Timer semaphoreTimer = new Trs.Timer(15000) { AutoReset = false}; //15K milliseconds is 15 seconds.

        #endregion

        #region Constructors
        public MicroClient(string base_address)
        {
            Id = Guid.NewGuid().ToString();
            _base_uri = getBaseUri(base_address);
            if (_base_uri == null)
            {
                Debug.WriteLine($@"ERROR: Base URI is null. MicroClient cannot be created.");
                return;
            }
            BaseClient = new HttpClient(); //Base client is read only. So initiate only once.
            ResetClientHeaders();
            semaphoreTimer.Elapsed += SemaPhoreTimer_Elapsed;
        }

        private void SemaPhoreTimer_Elapsed(object sender, Trs.ElapsedEventArgs e)
        {
            WriteTimerDebugMessage("Timer Elapsed","Elapsed call.");
            UnBlockClient("Elapsed Call");
        }

        public MicroClient(Uri base_uri) : this(base_uri.AbsoluteUri) { }
        #endregion

        #region FluentMethods
        
        public IClient ResetClientHeaders()
        {
            //remains the same throught the life time of this client.
            BaseClient.BaseAddress = _base_uri;
            BaseClient.DefaultRequestHeaders.Accept.Clear();
            BaseClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
            //BaseClient.DefaultRequestHeaders.Accept.Add(
            //    new MediaTypeWithQualityHeaderValue("application/x-www-form-urlencoded"));
            return this;
        }
        public IClient ClearRequestHeaders()
        {
            _requestHeaders = new ConcurrentDictionary<string, IEnumerable<string>>(); //Clear the requestheaders.
            return this;
        }
        public IClient AddRequestHeaders(string name, string value)
        {
            _requestHeaders?.TryAdd(name, new List<string>() { value });
            return this;
        }
        public IClient AddRequestHeaders(string name, List<string> values)
        {
            _requestHeaders?.TryAdd(name, values);
            return this;
        }
        /// <summary>
        /// This authentication will NOT be added to the headers,as the client is re-used. This will be added to each request header (if authorization is requested).
        /// </summary>
        /// <param name="token"></param>
        /// <param name="token_prefix"></param>
        /// <param name="name"></param>
        /// <returns></returns>
        public IClient AddRequestAuthentication(string token, string token_prefix = "Bearer")
        {
            request_token = _getJWT(token, token_prefix);
            return this;
        }
        public IClient ClearRequestAuthentication()
        {
            request_token = string.Empty;
            return this;
        }
        public IClient AddClientHeaderAuthentication(string token, string token_prefix = "Bearer")
        {
            ResetClientHeaders(); //Re initiate the client (clearing old headers)
            var _headerToken = _getJWT(token, token_prefix);
            if (!string.IsNullOrWhiteSpace(_headerToken))
            {
                //If it is null, then do not set anything. However, it would have already been cleared.
                //BaseClient.DefaultRequestHeaders.Add("Authorization", _headerToken);
                BaseClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(_headerToken);
            }
            return this;
        }
        #endregion

        #region Get Methods
        public async Task<SerializedResponse<T>> GetAsync<T>(string resource_url) where T : class
        {
            return await GetAsync<T>(resource_url, null);
        }
        public async Task<StringResponse> GetAsync(string resource_url)
        {
            return await GetAsync(resource_url,null);
        }
        public async Task<StringResponse> GetAsync(string resource_url, Dictionary<string, string> parameters)
        {
            return await GetAsync<string>(resource_url, parameters);
        }
        public async Task<SerializedResponse<T>> GetAsync<T>(string resource_url, Dictionary<string, string> parameters) where T : class
        {
            List<RestParam> paramslist = new List<RestParam>();
            if (parameters != null && parameters?.Count > 0)
            {
                foreach (var kvp in parameters)
                {
                    // For get, all the entries are in query string. Since we get the dictionary as string, we don't need serialization.
                    paramslist.Add(new RestParam(kvp.Key, kvp.Value, true, ParamType.QueryString));
                }
            }

            SerializedResponse<T> result = new SerializedResponse<T>();
            var _response = await SendAsync(resource_url, paramslist, Method.Get);
            result.OriginalResponse = _response.OriginalResponse; //Set the original response.
            //Response we receive will be base response.

            if (_response.IsSuccess)
            {
                var _cntnt = _response.Content;
                var _strCntnt = await _cntnt.ReadAsStringAsync();
                result.StringContent = _strCntnt;
                try
                {
                    if (typeof(T) == typeof(string))
                    {
                        result.SerializedContent = _strCntnt as T;
                    }
                    else
                    {
                        result.SerializedContent = JsonSerializer.Deserialize<T>(_strCntnt);
                    }
                }
                catch (Exception)
                {
                    result.SerializedContent = null; //Since it is a class, it should be nullable.
                }
            }
            return result;
        }
        
        #endregion

        #region Post Methods
        public async Task<IResponse> PostAsync(string resource_url, object content, bool is_serialized = false) 
        {
            return await PostAsync(resource_url, new RestParam("data", content, is_serialized, ParamType.RequestBody));
        }
        public async Task<IResponse> PostAsync(string resource_url, RestParam param)
        {
            return await PostAsync(resource_url, new List<RestParam>() { param });
        }
        public async Task<IResponse> PostAsync(string resource_url, IEnumerable<RestParam> param_list)
        {
            return await SendAsync(resource_url, paramList: param_list, Method.Post);
        }
        #endregion

        #region Send Methods
        public async Task<IResponse> SendAsync(string url, object content, Method method = Method.Get, ParamType param_type = ParamType.Default, bool is_serialized = false)
        {
            if (param_type == ParamType.Default)
            {
                //User has not set the type specifically, We try to identify which type is best in this situation.
                //When we have a single content, then we decide if we need to add it as a header value or post body.
                switch (method)
                {
                    case Method.Post:
                    case Method.Delete:
                    case Method.Update:
                        param_type = ParamType.RequestBody; //if post, we then set the parameter as body
                        break;
                    case Method.Get:
                        param_type = ParamType.QueryString; //if post, we then set the parameter as body
                        break;
                }
            }

            return await SendAsync(url, new RestParam("data", content, is_serialized, param_type), method);
        }
        public async Task<IResponse> SendAsync(string url, RestParam param, Method method = Method.Get)
        {
            //Just add this single param as a list to the send method.
            return await SendAsync(url, new List<RestParam>() { param }, method);
        }
        public async Task<IResponse> SendAsync(string url, IEnumerable<RestParam> paramList, Method method = Method.Get)
        {
            string inputURL = url;
            //Here, we create content and also modify the URL (if required).
            var processedInputs = processInputs(inputURL, paramList, method);
            return await SendAsync(processedInputs.url, processedInputs.content, method);
        }
        public async Task<IResponse> SendAsync(string url, HttpContent content, Method method = Method.Get)
        {
            //1. Here, we do not add anything to the URL or Content.
            //2. We just validate the URl and get the path and query part.
            //3. Add request headers and Authentication (if available).
            HttpMethod request_method = HttpMethod.Get;
            switch (method)
            {
                case Method.Get:
                    request_method = HttpMethod.Get;
                    break;
                case Method.Post:
                    request_method = HttpMethod.Post;
                    break;
                case Method.Delete:
                    request_method = HttpMethod.Delete;
                    break;
                case Method.Update:
                    request_method = HttpMethod.Put;
                    break;
            }
            //At this point, do not parse the URL. It might already contain the URL params added to it. So just call the URL. // parseURI(url).resource_part
            var request = new HttpRequestMessage(request_method, parseURI(url).pathQuery) { Content = content }; //URL should not have base part.

            //If the request has some kind of request headers, then add them.
            if (!string.IsNullOrWhiteSpace(request_token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue(request_token);
            }

            //Add other request headers if available.
            if (_requestHeaders != null && _requestHeaders?.Count > 0)
            {
                foreach (var kvp in _requestHeaders)
                {
                    try
                    {
                        request.Headers.Add(kvp.Key, kvp.Value);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(ex.ToString());
                    }
                }
            }

            return await SendAsync(request);
        }
        public async Task<IResponse> SendAsync(HttpRequestMessage request)
        {
            //Here we donot modify anything. We just send and receive the response.

            HttpResponseMessage message;
            if (add_cancellation_token)
            {
                message = await BaseClient.SendAsync(request,cancellation_token);
                //After the token is added, we just remove it.
                cancellation_token = default(CancellationToken);
                add_cancellation_token = false;
            }
            else
            {
                message = await BaseClient.SendAsync(request);
            }

            ////After you have send the request, there is no need to block any other thread, since the private variables would have been consumed. So release them.
            //UnBlockClient();

            var _response = new BaseResponse() { OriginalResponse = message };
            return _response;
        }
        #endregion

        #region Implemented Methods
        public IClient AddRequestCancellationToken(CancellationToken token)
        {
            //Adds only for this request.
            cancellation_token = token;
            add_cancellation_token = true;
            return this;
        }
        public Dictionary<string, IEnumerable<string>> GetRequestHeaders()
        {
            try
            {
                return _requestHeaders?.ToDictionary(p => p.Key, p => p.Value);
            }
            catch (Exception)
            {
                return null;
            }
        }

        #endregion

        #region ThreadSafe Implementation
        private void WriteBlockDebugMessage(string title,string message = null)
        {
            string towrite = $@"Microclient ==> {title}: Count : {requestSemaphore.CurrentCount} at  {DateTime.Now.ToLongTimeString()} for client {Id} with address {_base_uri}";
            if (!string.IsNullOrWhiteSpace(message))
            {
                towrite = towrite + $@" ===> {message}";
            }
            Debug.WriteLine(towrite);
        }
        private void WriteTimerDebugMessage(string title, string message = null)
        {
            string towrite = $@"Microclient ==> {title} with {semaphoreTimer.Interval} milliseconds : at {DateTime.Now.ToLongTimeString()} for client {Id} with address {_base_uri}";
            if (!string.IsNullOrWhiteSpace(message))
            {
                towrite = towrite + $@" ===> {message}";
            }
            Debug.WriteLine(towrite);
        }

        public IClient BlockClient(string message = null)
        {
            return BlockClient(0,message);
        }
        public IClient BlockClient(double block_seconds = 15, string message = null)
        {
            BlockClientAsync(block_seconds,message).Wait();
            return this; //Block and return this client. So no other thread can use until this is unblocked.
        }
        public async Task BlockClientAsync(string message = null)
        {
            await BlockClientAsync(0,message);
        }
        public async Task BlockClientAsync(double block_seconds = 15, string message = null)
        {
            WriteBlockDebugMessage("Waiting",message);
            await requestSemaphore.WaitAsync(); //All requests will wait here.
            WriteBlockDebugMessage("Blocked",message);
            semaphoreTimer.Stop(); //It it is running for someother reason.
            if (block_seconds > 0)
            {
                semaphoreTimer.Interval = block_seconds * 1000.0; //If the interval is 0 , then we donot start the timer. change seconds into milliseconds.
                WriteTimerDebugMessage("Timer Started",message);
                semaphoreTimer.Start(); //Star the timer.
            }
        }
        public IClient UnBlockClient(string message = null)
        {
            if (!requestSemaphore.Wait(0)) //Just to check if we are able to enter the current thread. 
            {
                if (semaphoreTimer.Enabled)
                {
                    semaphoreTimer.Stop(); //If we prematurely decide to Unblock the client, the timer can be stopped.
                    WriteTimerDebugMessage("Timer Stopped",message);
                }
                
                //If we are not able to enter inside then it means that we already have some other process going on inside. We just release it.
                requestSemaphore.Release();
                WriteBlockDebugMessage("Released", message);
            }
            return this;
        }
        #endregion

        #region Helpers

        private string _getJWT(string token, string token_prefix)
        {
            try
            {
                var _token = token_prefix ?? "";
                _token = _token + " " + token;
                return _token?.Trim();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private HttpContent _createContent(IEnumerable<RestParam> paramList, Method method)
        {
            try
            {
                HttpContent result = null;

                var target = paramList.FirstOrDefault(p => p.ParamType == ParamType.RequestBody);

                if (target == null)
                {
                    //Process and add this content to the body as required.
                    return new StringContent(string.Empty, System.Text.Encoding.UTF8, "application/json");
                }

                result = new StringContent(target.IsSerialized?target.Value as string:target.ToJson(), Encoding.UTF8, "application/json");
                return result;
            }
            catch (Exception ex)
            {
                return null;
            }
        }

        private string _createQuery(string url, IEnumerable<RestParam> paramList)
        {
            var _query = HttpUtility.ParseQueryString(url);

            //Assuming all the inputs are serialzied or direct values.
            foreach (var param in paramList.Where(p=>p.ParamType == ParamType.QueryString))
            {
                _query[param.Key] = param.Value as string;
            }
            return _query.ToString();
        }

        private (HttpContent content,string url) processInputs(string url, IEnumerable<RestParam> paramList, Method method)
        {
            try
            {
                //HTTPCONENT itself is a abstract class. We can have StringContent, StreamContent,FormURLEncodedContent,MultiPartFormdataContent.
                //Based on the params, we might add the data to content or to the url (in case of get).
                if (paramList == null || paramList?.Count() == 0) return (null,url);
                HttpContent processed_content = null;
                string processed_url = url;

                //If this is a get method, do not waste time in processing the parameter list.
                if (method != Method.Get)
                {
                    processed_content = _createContent(paramList, method);
                }

                processed_url = _createQuery(url, paramList);
                return (processed_content, processed_url);
            }
            catch (Exception ex )
            {
                throw ex;
            }
        }
        private (string authority, string pathQuery) parseURI(string input_url)
        {
            try
            {
                if (string.IsNullOrEmpty(input_url)) return (null, null);
                if(Uri.TryCreate(input_url,UriKind.RelativeOrAbsolute,out Uri _uri))
                {
                    if (_uri.IsAbsoluteUri)
                    {
                        string _authority = _uri.GetLeftPart(UriPartial.Authority);
                        string _method = input_url.Substring(_authority.Length);
                        return (_authority, _method);
                    }
                }
                return (null, input_url);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ERROR: " + ex.StackTrace);
                return (null, input_url);
            }
        }
        private (string authority, string pathQuery) parseURI(Uri input_uri)
        {
            return parseURI(input_uri.AbsoluteUri);
        }
        private Uri getBaseUri(string address)
        {
            bool result = Uri.TryCreate(address, UriKind.Absolute, out var uri_result)
                && (uri_result.Scheme == Uri.UriSchemeHttp || uri_result.Scheme == Uri.UriSchemeHttps);
            if (result) return uri_result;
            Console.WriteLine($@"ERROR: Unable to create URI from the address {address}");
            return null;
        }
        private Uri getBaseUri(Uri inputURI)
        {
            return getBaseUri(inputURI.AbsoluteUri);
        }
        #endregion
    }
}
