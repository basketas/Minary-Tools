﻿namespace HttpReverseProxyLib
{
  using HttpReverseProxyLib.DataTypes;
  using HttpReverseProxyLib.DataTypes.Class;
  using HttpReverseProxyLib.DataTypes.Enum;
  using HttpReverseProxyLib.Exceptions;
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using System.Net;
  using System.Text;
  using System.Text.RegularExpressions;


  public class IncomingClientRequest
  {

    #region MEMBERS

    #endregion


    #region PUBLIC METHODS

    /// <summary>
    /// Initializes a new instance of the <see cref="IncomingClientRequest"/> class.
    ///
    /// </summary>
    public IncomingClientRequest()
    {
    }


    public void ReceiveClientRequestLine(RequestObj requestObj)
    {
      // Read client HTTP request line (the GET/POST/PUT/... line)
      requestObj.ClientRequestObj.RequestLine = requestObj.ClientRequestObj.ClientBinaryReader.ReadClientRequestLine(false);

      // If request line was empty throw a recoverable exception
      // https://www.w3.org/Protocols/rfc2616/rfc2616-sec4.html#sec4
      // In the interest of robustness, servers SHOULD ignore any empty line(s) received where a Request-Line is expected. 
      // In other words, if the server is reading the protocol stream at the beginning of a message and receives a CRLF first, it should ignore the CRLF. 
      if (string.IsNullOrEmpty(requestObj.ClientRequestObj.RequestLine.RequestLine) == true)
      {
        throw new EmptyRequestException($"{requestObj.SrcIp} sent empty request (RFC2616)");
      }
      
      Logging.Instance.LogMessage(requestObj.Id, requestObj.ProxyProtocol, Loglevel.Debug, "IncomingClientRequest.ReceiveClientRequestHeaders() : StatusLine={0}", requestObj.ClientRequestObj?.RequestLine?.RequestLine);
      Logging.Instance.LogMessage(requestObj.Id, requestObj.ProxyProtocol, Loglevel.Debug, "IncomingClientRequest.ReceiveClientRequestHeaders() : NewlineType={0}", requestObj.ClientRequestObj?.RequestLine?.NewlineType);
            
      // 2. Parse and verify request line parameters
      this.ParseRequestString(requestObj);
    }


    /// <summary>
    ///
    /// </summary>
    /// <param name="requestObj"></param>
    public void ReceiveClientRequestHeaders(RequestObj requestObj)
    {
      // Read the client request headers
      this.ParseClientRequestHeaders(requestObj);

      // If Host header does not exist throw exception
      if (!requestObj.ClientRequestObj.ClientRequestHeaders.ContainsKey("Host"))
      {
        ClientNotificationException exception = new ClientNotificationException();
        exception.Data.Add(StatusCodeLabel.StatusCode, HttpStatusCode.NotFound);
        throw exception;
      }

      // if Host header contains illegal characters throw exception
      if (Regex.Match(requestObj.ClientRequestObj.ClientRequestHeaders["Host"][0], @"[^\w\d\-_\.]+").Success == true)
      {
        ClientNotificationException exception = new ClientNotificationException("Invalid characters in host name");
        exception.Data.Add(StatusCodeLabel.StatusCode, HttpStatusCode.BadRequest);
        throw exception;
      }

      requestObj.ClientRequestObj.Host = requestObj.ClientRequestObj.ClientRequestHeaders["Host"][0];

      // Parse Client request content type
      requestObj.ClientRequestObj.ContentTypeEncoding = this.DetermineClientRequestContentTypeEncoding(requestObj);

      // Parse Client request content length
      this.DetermineClientRequestContentLength(requestObj);

      requestObj.ProxyDataTransmissionModeC2S = this.DetermineDataTransmissionModeC2S(requestObj);
      Logging.Instance.LogMessage(requestObj.Id, requestObj.ProxyProtocol, Loglevel.Debug, "ReceiveClientRequestHeaders(): ProxyDataTransmissionModeC2S:{0}", requestObj.ProxyDataTransmissionModeC2S.ToString());
    }

    /*
     * Http2http2XX                 -> Relay response                                   SendServerResponseData2Client()
     * Http2Http3XX                 -> Relay response                                   SendServerResponseData2Client()
     * Http2Https3XXSameUrl         -> Remember redirect, strip SSL, request new Url    SSLCacheAndRedirectClient2RedirectLocation()
     * Http2Https3XXDifferentUrl    -> Remember redirect, strip SSL, relay response
     *
     * Process HTTP request.
     * 1. Detect HTTP Redirect requests
     * 2. Cache new HTTP Redirect locations
     * 3. Recognize incoming lClient requests that need to be SSL-Stripped
     * 4. Process reqular HTTP requests
     *
     */

    #endregion


    #region PRIVATE METHODS

    /// <summary>
    ///
    /// </summary>
    /// <param name="requestString"></param>
    private void ParseRequestString(RequestObj requestObj)
    {
      if (requestObj == null)
      {
        throw new Exception("Request object is invalid");
      }

      if (string.IsNullOrEmpty(requestObj?.ClientRequestObj?.RequestLine?.RequestLine))
      {
        ClientNotificationException exception = new ClientNotificationException("The RequestLine is undefined");
        exception.Data.Add(StatusCodeLabel.StatusCode, HttpStatusCode.BadRequest);
        throw exception;
      }
      
      if (!requestObj.ClientRequestObj.RequestLine.RequestLine.Contains(' '))
      {
        ClientNotificationException exception = new ClientNotificationException();
        exception.Data.Add(StatusCodeLabel.StatusCode, HttpStatusCode.BadRequest);
        throw exception;
      }
      
      string[] requestSplitBuffer = requestObj.ClientRequestObj.RequestLine.RequestLine.Split(new char[] { ' ' }, 3);
      if (requestSplitBuffer.Count() != 3)
      {
        ClientNotificationException exception = new ClientNotificationException();
        exception.Data.Add(StatusCodeLabel.StatusCode, HttpStatusCode.BadRequest);
        throw exception;
      }
      
      if (!Regex.Match(requestSplitBuffer[0].ToLower(), @"^\s*(get|put|post|head|trace|delete|options|connect)\s*$").Success)
      {
        ClientNotificationException exception = new ClientNotificationException();
        exception.Data.Add(StatusCodeLabel.StatusCode, HttpStatusCode.MethodNotAllowed);
        throw exception;
      }
      
      if (!requestSplitBuffer[1].StartsWith("/"))
      {
        ClientNotificationException exception = new ClientNotificationException();
        exception.Data.Add(StatusCodeLabel.StatusCode, HttpStatusCode.BadRequest);
        throw exception;
      }
      
      if (!requestSplitBuffer[2].StartsWith("HTTP/1."))
      {
        ClientNotificationException exception = new ClientNotificationException();
        exception.Data.Add(StatusCodeLabel.StatusCode, HttpStatusCode.HttpVersionNotSupported);
        throw exception;
      }

      // Evaluate request method
      requestObj.ClientRequestObj.RequestLine.MethodString = requestSplitBuffer[0];
      requestObj.ClientRequestObj.RequestLine.Path = requestSplitBuffer[1];
      requestObj.ClientRequestObj.RequestLine.HttpVersion = requestSplitBuffer[2];
      
      if (requestObj.ClientRequestObj.RequestLine.MethodString == "GET")
      {
        requestObj.ClientRequestObj.RequestLine.RequestMethod = RequestMethod.GET;
      }
      else if (requestObj.ClientRequestObj.RequestLine.MethodString == "POST")
      {
        requestObj.ClientRequestObj.RequestLine.RequestMethod = RequestMethod.POST;
      }
      else if (requestObj.ClientRequestObj.RequestLine.MethodString == "HEAD")
      {
        requestObj.ClientRequestObj.RequestLine.RequestMethod = RequestMethod.HEAD;
      }
      else if (requestObj.ClientRequestObj.RequestLine.MethodString == "PUT")
      {
        requestObj.ClientRequestObj.RequestLine.RequestMethod = RequestMethod.PUT;
        ClientNotificationException exception = new ClientNotificationException();
        exception.Data.Add(StatusCodeLabel.StatusCode, HttpStatusCode.MethodNotAllowed);
        throw exception;
      }
      else if (requestObj.ClientRequestObj.RequestLine.MethodString == "DELETE")
      {
        requestObj.ClientRequestObj.RequestLine.RequestMethod = RequestMethod.DELETE;
        ClientNotificationException exception = new ClientNotificationException();
        exception.Data.Add(StatusCodeLabel.StatusCode, HttpStatusCode.MethodNotAllowed);
        throw exception;
      }
      else if (requestObj.ClientRequestObj.RequestLine.MethodString == "OPTIONS")
      {
        requestObj.ClientRequestObj.RequestLine.RequestMethod = RequestMethod.OPTIONS;
        ClientNotificationException exception = new ClientNotificationException();
        exception.Data.Add(StatusCodeLabel.StatusCode, HttpStatusCode.MethodNotAllowed);
        throw exception;
      }
      else
      {
        requestObj.ClientRequestObj.RequestLine.RequestMethod = RequestMethod.Undefined;
      }
      
      if (!requestObj.ClientRequestObj.RequestLine.Path.StartsWith("/"))
      {
        requestObj.ClientRequestObj.RequestLine.Path = $"/{requestObj.ClientRequestObj.RequestLine.Path}";
      }
      
      requestObj.HttpLogData = requestObj.ClientRequestObj.RequestLine.RequestLine.Trim();
    }


    /// <summary>
    ///
    /// </summary>
    /// <param name="requestObj"></param>
    private void ParseClientRequestHeaders(RequestObj requestObj)
    {
      string httpHeader;
      int contentLen = 0;

      do
      {
        httpHeader = requestObj.ClientRequestObj.ClientBinaryReader.ReadLine(false);

        if (string.IsNullOrEmpty(httpHeader))
        {
          Logging.Instance.LogMessage(requestObj.Id, requestObj.ProxyProtocol, Loglevel.Debug, "HttpReverseProxyLib.ParseClientRequestHeaders(): All headers read", httpHeader);
          break;
        }

        Logging.Instance.LogMessage(requestObj.Id, requestObj.ProxyProtocol, Loglevel.Debug, "HttpReverseProxyLib.ParseClientRequestHeaders(): Client request header: {0}", httpHeader);
        if (!httpHeader.Contains(':'))
        {
          Logging.Instance.LogMessage(requestObj.Id, requestObj.ProxyProtocol, Loglevel.Debug, "HttpReverseProxyLib.ParseClientRequestHeaders(): Invalid header |{0}|", httpHeader);
          continue;
        }

        string[] httpHeaders = httpHeader.Split(new string[] { ":" }, 2, StringSplitOptions.None);
        httpHeaders[0] = httpHeaders[0].Trim();
        httpHeaders[1] = httpHeaders[1].Trim();

        if (!requestObj.ClientRequestObj.ClientRequestHeaders.ContainsKey(httpHeaders[0]))
        {
          requestObj.ClientRequestObj.ClientRequestHeaders.Add(httpHeaders[0], new List<string>());
        }

        switch (httpHeaders[0].ToLower())
        {
          case "host":
            requestObj.ClientRequestObj.ClientRequestHeaders["Host"].Add(httpHeaders[1]);
            break;
          case "user-agent":
            requestObj.ClientRequestObj.ClientRequestHeaders["User-Agent"].Add(httpHeaders[1]);
            break;
          case "accept":
            requestObj.ClientRequestObj.ClientRequestHeaders["Accept"].Add(httpHeaders[1]);
            break;
          case "referer":
            requestObj.ClientRequestObj.ClientRequestHeaders["Referer"].Add(httpHeaders[1]);
            break;
          case "cookie":
            requestObj.ClientRequestObj.ClientRequestHeaders["Cookie"].Add(httpHeaders[1]);
            break;
          case "connection":
            requestObj.ClientRequestObj.ClientRequestHeaders["Connection"].Add(httpHeaders[1]);
            if (httpHeaders[1].ToLower().Trim() == "close")
            {
              requestObj.ClientRequestObj.IsClientKeepAlive = false;
            }
            else
            {
              requestObj.ClientRequestObj.IsClientKeepAlive = true;
            }

            break;

          // Ignore these
          //case "proxy-connection":
          //case "keep-alive":
          //case "upgrade-insecure-requests":
          //  break;

          case "content-length":
            int.TryParse(httpHeaders[1], out contentLen);
            requestObj.ClientRequestObj.ContentLength = contentLen;
            requestObj.ClientRequestObj.ClientRequestHeaders["Content-Length"].Add(httpHeaders[1]);
            break;
          case "content-type":
            requestObj.ClientRequestObj.ClientRequestHeaders["Content-Type"].Add(httpHeaders[1]);
            break;
          case "if-modified-since":
            string[] sb = httpHeaders[1].Trim().Split(new char[] { ';' });
            DateTime d;
            if (DateTime.TryParse(sb[0], out d))
            {
              requestObj.ClientRequestObj.ClientRequestHeaders["If-Modified-Since"].Add(httpHeaders[1]);
            }

            break;
          default:
            try
            {
              requestObj.ClientRequestObj.ClientRequestHeaders[httpHeaders[0]].Add( httpHeaders[1]);
            }
            catch (Exception ex)
            {
              Logging.Instance.LogMessage(requestObj.Id, requestObj.ProxyProtocol, Loglevel.Error, "IncomingClientRequest.ParseClientRequestHeaders(EXCEPTION) : {0}", ex.Message);
            }

            break;
        }
      }
      while (!string.IsNullOrWhiteSpace(httpHeader));
    }


    private DataContentTypeEncoding DetermineClientRequestContentTypeEncoding(RequestObj requestObj)
    {
      DataContentTypeEncoding contentTypeEncoding = new DataContentTypeEncoding();

      if (requestObj == null)
      {
        throw new ProxyWarningException("Request object is invalid");
      }

      if (requestObj.ClientRequestObj == null)
      {
        throw new ProxyWarningException("Client request object is invalid");
      }

      if (requestObj.ClientRequestObj.ClientRequestHeaders == null)
      {
        throw new ProxyWarningException("The headers list is invalid");
      }

      // If there is no content type headerByteArray set the default values
      if (!requestObj.ClientRequestObj.ClientRequestHeaders.ContainsKey("Content-Type") ||
          string.IsNullOrEmpty(requestObj.ClientRequestObj.ClientRequestHeaders["Content-Type"][0]))
      {
        contentTypeEncoding.ContentType = "text/html";
        contentTypeEncoding.ContentCharSet = "UTF-8";
        contentTypeEncoding.ContentCharsetEncoding = Encoding.GetEncoding(contentTypeEncoding.ContentCharSet);

        Logging.Instance.LogMessage(requestObj.Id, requestObj.ProxyProtocol, Loglevel.Debug, "IncomingClientRequest.DetermineClientRequestContentTypeEncoding(): No Content-Type header found: text/html, UTF-8");
        return contentTypeEncoding;
      }

      // Parse the server response content type
      try
      {
        string contentType = requestObj.ClientRequestObj.ClientRequestHeaders["Content-Type"][0];

        if (contentType.Contains(";"))
        {
          string[] splitter = contentType.Split(new char[] { ';' }, 2);
          contentTypeEncoding.ContentType = splitter[0];
          contentTypeEncoding.ContentCharSet = this.DetermineContentCharSet(splitter[1]);
          contentTypeEncoding.ContentCharsetEncoding = Encoding.GetEncoding(contentTypeEncoding.ContentCharSet);
          Logging.Instance.LogMessage(requestObj.Id, requestObj.ProxyProtocol, Loglevel.Debug, "IncomingClientRequest.DetermineClientRequestContentTypeEncoding(): Content-Type/Charset header found: {0}, {1}", contentTypeEncoding.ContentType, contentTypeEncoding.ContentCharSet);
        }
        else
        {
          contentTypeEncoding.ContentType = contentType;
          contentTypeEncoding.ContentCharSet = "UTF-8";
          contentTypeEncoding.ContentCharsetEncoding = Encoding.GetEncoding(contentTypeEncoding.ContentCharSet);
          Logging.Instance.LogMessage(requestObj.Id, requestObj.ProxyProtocol, Loglevel.Debug, "IncomingClientRequest.DetermineClientRequestContentTypeEncoding(): Content-Type (noCharset) header found: {0}, {1}", contentTypeEncoding.ContentType, contentTypeEncoding.ContentCharSet);
        }
      }
      catch (Exception ex)
      {
        contentTypeEncoding.ContentType = "text/html";
        contentTypeEncoding.ContentCharSet = "UTF-8";
        contentTypeEncoding.ContentCharsetEncoding = Encoding.GetEncoding(contentTypeEncoding.ContentCharSet);
        Logging.Instance.LogMessage(requestObj.Id, requestObj.ProxyProtocol, Loglevel.Debug, "IncomingClientRequest.DetermineClientRequestContentTypeEncoding(Exception): text/html, UTF-8 {0}", ex.Message);
      }

      return contentTypeEncoding;
    }


    private string DetermineContentCharSet(string httpContentTypeHeader)
    {
      string determinedCharSet = "UTF-8";

      if (string.IsNullOrEmpty(httpContentTypeHeader))
      {
        throw new Exception("The char set headerByteArray is invalid");
      }

      httpContentTypeHeader = httpContentTypeHeader.Trim();
      if (Regex.Match(httpContentTypeHeader, @"^charset\s*=", RegexOptions.IgnoreCase).Success)
      {
        string[] splitter = httpContentTypeHeader.Split(new char[] { '=' }, 2);
        determinedCharSet = splitter[1];
      }

      return determinedCharSet;
    }


    /// <summary>
    ///
    /// </summary>
    /// <param name="requestObj"></param>
    private void DetermineClientRequestContentLength(RequestObj requestObj)
    {
      try
      {
        if (requestObj.ClientRequestObj.ClientRequestHeaders.ContainsKey("Content-Length"))
        {
          string contentLen = requestObj.ClientRequestObj.ClientRequestHeaders["Content-Length"][0];
          requestObj.ClientRequestObj.ContentLength = int.Parse(contentLen);
        }
        else if (requestObj.ClientRequestObj.ClientRequestHeaders.ContainsKey("Transfer-Encoding"))
        {
          requestObj.ClientRequestObj.ContentLength = -1;
        }
        else
        {
          requestObj.ClientRequestObj.ContentLength = 0;
        }
      }
      catch (Exception ex)
      {
        Logging.Instance.LogMessage(requestObj.Id, requestObj.ProxyProtocol, Loglevel.Warning, "IncomingClientRequest.DetermineClientRequestContentLength(Exception): {0}", ex.Message);
        requestObj.ClientRequestObj.ContentLength = 0;
      }
    }


    private DataTransmissionMode DetermineDataTransmissionModeC2S(RequestObj requestObj)
    {

      // Transfer behavior is "Content-Length"
      if (requestObj.ClientRequestObj.ClientRequestHeaders.ContainsKey("Content-Length"))
      {
        string contentLen = requestObj.ClientRequestObj.ClientRequestHeaders["Content-Length"][0];
        requestObj.ClientRequestObj.ContentLength = int.Parse(contentLen);
Logging.Instance.LogMessage(requestObj.Id, requestObj.ProxyProtocol, Loglevel.Debug, "IncomingClientRequest.DetermineDataTransmissionModeC2S(): ContainsKey(Content-Length)");

        if (requestObj.ClientRequestObj.ContentLength > 0)
        {
Logging.Instance.LogMessage(requestObj.Id, requestObj.ProxyProtocol, Loglevel.Debug, "IncomingClientRequest.DetermineDataTransmissionModeC2S(): ClientRequestContentLength > 0");
          return DataTransmissionMode.FixedContentLength;
        }
        else if (requestObj.ClientRequestObj.ContentLength == 0)
        {
Logging.Instance.LogMessage(requestObj.Id, requestObj.ProxyProtocol, Loglevel.Debug, "IncomingClientRequest.DetermineDataTransmissionModeC2S(): ClientRequestContentLength == 0");
          return DataTransmissionMode.NoDataToTransfer;
        }
        else
        {
Logging.Instance.LogMessage(requestObj.Id, requestObj.ProxyProtocol, Loglevel.Debug, "IncomingClientRequest.DetermineDataTransmissionModeC2S(): ClientRequestContentLength > 0");
          return DataTransmissionMode.Error;
        }

      // Transfer behavior is "Chunked"
      }
      else if (requestObj.ClientRequestObj.ClientRequestHeaders.ContainsKey("Transfer-Encoding"))
      {
        Logging.Instance.LogMessage(requestObj.Id, requestObj.ProxyProtocol, Loglevel.Debug, "IncomingClientRequest.DetermineDataTransmissionModeC2S(): ContainsKey(ransfer-Encoding)");
        return DataTransmissionMode.Chunked;

      // Transfer behavior is "Relay blindly"
      }
      else if (requestObj.ClientRequestObj.RequestLine.RequestMethod == RequestMethod.POST)
      {
        Logging.Instance.LogMessage(requestObj.Id, requestObj.ProxyProtocol, Loglevel.Debug, "IncomingClientRequest.DetermineDataTransmissionModeC2S(): ReadOneLine");
        return DataTransmissionMode.ReadOneLine;

      // No data to transfer
      }
      else
      {
        Logging.Instance.LogMessage(requestObj.Id, requestObj.ProxyProtocol, Loglevel.Debug, "IncomingClientRequest.DetermineDataTransmissionModeC2S(): NoDataToTransfer");
        return DataTransmissionMode.NoDataToTransfer;
      }
    }

    #endregion

  }
}
