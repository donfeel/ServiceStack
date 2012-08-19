﻿using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using ServiceStack.Common;
using ServiceStack.Configuration;
using ServiceStack.Html;
using ServiceStack.Logging;
using ServiceStack.Razor.Compilation.CSharp;
using ServiceStack.Razor.Templating;
using ServiceStack.Razor.VirtualPath;
using ServiceStack.ServiceHost;
using ServiceStack.Text;
using ServiceStack.WebHost.Endpoints;
using ServiceStack.WebHost.Endpoints.Extensions;

namespace ServiceStack.Razor
{
    public enum RazorPageType
    {
        ContentPage = 1,
        ViewPage = 2,
        SharedViewPage = 3,
        Template = 4,
    }

    public class RazorFormat : IRazorViewEngine, IPlugin, IRazorPlugin
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(RazorFormat));

        public const string NamespacesAppSettingsKey = "servicestack.razor.namespaces";

        private static RazorFormat instance;
        public static RazorFormat Instance
        {
            get { return instance ?? (instance = new RazorFormat()); }
        }

        private const string ErrorPageNotFound = "Could not find Razor page '{0}'";

        public static string TemplateName = "_Layout.cshtml";
        public static string TemplatePlaceHolder = "@RenderBody()";

        // ~/View - Dynamic Pages
        public Dictionary<string, ViewPageRef> ViewPages = new Dictionary<string, ViewPageRef>(
            StringComparer.CurrentCultureIgnoreCase);

        // ~/View/Shared - Dynamic Shared Pages
        public Dictionary<string, ViewPageRef> ViewSharedPages = new Dictionary<string, ViewPageRef>(
            StringComparer.CurrentCultureIgnoreCase);

        //Content Pages outside of ~/View
        public Dictionary<string, ViewPageRef> ContentPages = new Dictionary<string, ViewPageRef>(
            StringComparer.CurrentCultureIgnoreCase);

        public Dictionary<string, ViewPageRef> MasterPageTemplates = new Dictionary<string, ViewPageRef>(
            StringComparer.CurrentCultureIgnoreCase);

        public IAppHost AppHost { get; set; }

        public Dictionary<string, string> ReplaceTokens { get; set; }

        public Func<string, IEnumerable<ViewPageRef>> FindRazorPagesFn { get; set; }

        public IVirtualPathProvider VirtualPathProvider { get; set; }

        public HashSet<string> TemplateNamespaces { get; set; }

        public bool WatchForModifiedPages { get; set; }

		public Dictionary<string, Type> RazorExtensionBaseTypes { get; set; }

		public Type DefaultBaseType
		{
			get
			{
				Type baseType;
				return RazorExtensionBaseTypes.TryGetValue("cshtml", out baseType) ? baseType : null;
			}
			set
			{
				RazorExtensionBaseTypes["cshtml"] = value;
			}
		}

		public TemplateService TemplateService
		{
			get
			{
				TemplateService templateService;
				return templateServices.TryGetValue("cshtml", out templateService) ? templateService : null;
			}
		}

        public RazorFormat()
        {
            this.WatchForModifiedPages = true;
            this.FindRazorPagesFn = FindRazorPages;
            this.ReplaceTokens = new Dictionary<string, string>();
            this.TemplateNamespaces = new HashSet<string> {
                "System",
                "System.Collections.Generic",
                "System.Linq",
                "ServiceStack.Html",
                "ServiceStack.Razor",
            };
			this.RazorExtensionBaseTypes = new Dictionary<string, Type>(StringComparer.CurrentCultureIgnoreCase) {
				{"cshtml", typeof(ViewPage<>) },
				{"rzr", typeof(ViewPage<>) },
			};
            RegisterNamespacesInConfig();
        }

        private void RegisterNamespacesInConfig()
        {
            //Infer from <system.web.webPages.razor> - what VS.NET's intell-sense uses
            var configPath = EndpointHostConfig.GetAppConfigPath();
            if (configPath != null)
            {
                var xml = configPath.ReadAllText();
                var doc = XElement.Parse(xml);
                doc.AnyElement("system.web.webPages.razor")
                    .AnyElement("pages")
                        .AnyElement("namespaces")
                            .AllElements("add").ToList()
                                .ForEach(x => TemplateNamespaces.Add(x.AnyAttribute("namespace").Value));
            }

            //E.g. <add key="servicestack.razor.namespaces" value="System,ServiceStack.Text" />
            if (ConfigUtils.GetNullableAppSetting(NamespacesAppSettingsKey) != null)
            {
                ConfigUtils.GetListFromAppSetting(NamespacesAppSettingsKey)
                    .ForEach(x => TemplateNamespaces.Add(x));
            }
        }

        public void Register(IAppHost appHost)
        {
            if (instance == null) instance = this;
            Configure(appHost);
        }

        public void Configure(IAppHost appHost)
        {
            this.AppHost = appHost;
            this.ReplaceTokens = new Dictionary<string, string>(appHost.Config.MarkdownReplaceTokens);
            if (!appHost.Config.WebHostUrl.IsNullOrEmpty())
                this.ReplaceTokens["~/"] = appHost.Config.WebHostUrl.WithTrailingSlash();

            if (VirtualPathProvider == null)
                VirtualPathProvider = new MultiVirtualPathProvider(AppHost,
                    new ResourceVirtualPathProvider(AppHost),
                    new FileSystemVirtualPathProvider(AppHost));

            Init();

			RegisterRazorPages(appHost.Config.RazorSearchPath);

            //Render HTML
            appHost.HtmlProviders.Add((requestContext, dto, httpRes) => {

                var httpReq = requestContext.Get<IHttpRequest>();
                ViewPageRef razorPage;
                if ((razorPage = GetViewPageByResponse(dto, httpReq)) == null)
                    return false;

                if (WatchForModifiedPages)
                    ReloadModifiedPageAndTemplates(razorPage);

                return ProcessRazorPage(httpReq, razorPage, dto, httpRes);
            });

            appHost.CatchAllHandlers.Add((httpMethod, pathInfo, filePath) => {
                ViewPageRef razorPage = null;

                if (filePath != null)
                    razorPage = GetContentPage(filePath.WithoutExtension());

                if (razorPage == null)
                    razorPage = GetContentResourcePage(pathInfo);

                if (razorPage == null)
                    razorPage = GetContentPage(pathInfo);

                return razorPage == null
                    ? null
                    : new RazorHandler {
                        RazorFormat = this,
                        RazorPage = razorPage,
                        RequestName = "RazorPage",
                        PathInfo = pathInfo,
                        FilePath = filePath
                    };
            });
        }

		private Dictionary<string, TemplateService> templateServices;
		private TemplateService[] templateServicesArray;

		public void Init()
        {
            //Force Binder to load
            var loaded = typeof(Microsoft.CSharp.RuntimeBinder.Binder).Assembly != null;
            if (!loaded)
                throw new ConfigurationErrorsException("Microsoft.CSharp not properly loaded");

			templateServices = new Dictionary<string, TemplateService>(StringComparer.CurrentCultureIgnoreCase);
			var compilerService = new CSharpDirectCompilerService();

			foreach (var entry in RazorExtensionBaseTypes)
			{
				var razorBaseType = entry.Value;
				if (razorBaseType != null && !razorBaseType.HasInterface(typeof(ITemplatePage)))
					throw new ConfigurationErrorsException(razorBaseType.FullName + " must inherit from RazorBasePage");

				var ext = entry.Key[0] == '.' ? entry.Key.Substring(1) : entry.Key;
				templateServices[ext] = new TemplateService(this, compilerService, razorBaseType) {
					Namespaces = TemplateNamespaces
				};
			}

			templateServicesArray = templateServices.Values.ToArray();
		}
		
		public IEnumerable<ViewPageRef> FindRazorPages(string dirPath)
        {
            var hasWebPages = false;
            foreach (var entry in templateServices)
			{
				var ext = entry.Key;
				var csHtmlFiles = VirtualPathProvider.GetAllMatchingFiles("*." + ext);
				foreach (var csHtmlFile in csHtmlFiles)
				{
					if (csHtmlFile.GetType() != typeof(ResourceVirtualFile))
						hasWebPages = true;
					
					var pageName = csHtmlFile.Name.WithoutExtension();
					var pageContents = csHtmlFile.ReadAllText();
					
					var pageType = RazorPageType.ContentPage;
					if (VirtualPathProvider.IsSharedFile(csHtmlFile))
						pageType = RazorPageType.SharedViewPage;
					else if (VirtualPathProvider.IsViewFile(csHtmlFile))
						pageType = RazorPageType.ViewPage;

					var templateService = entry.Value;
					templateService.RegisterPage(csHtmlFile.VirtualPath, pageName);

					yield return new ViewPageRef(this, csHtmlFile.VirtualPath, pageName, pageContents, pageType) {
						LastModified = csHtmlFile.LastModified,
						Service = templateService
					};
				}
			}

            if (!hasWebPages)
                WatchForModifiedPages = false;
        }

        public bool ProcessRazorPage(IHttpRequest httpReq, ViewPageRef razorPage, object dto, IHttpResponse httpRes)
        {
            //Add extensible way to control caching
            //httpRes.AddHeaderLastModified(razorPage.GetLastModified());

            var templatePath = razorPage.TemplatePath;
            if (httpReq != null && httpReq.QueryString["format"] != null)
            {
                if (!httpReq.GetFormatModifier().StartsWithIgnoreCase("bare"))
                    templatePath = null;
            }

            var template = ExecuteTemplate(dto, razorPage.PageName, templatePath, httpReq, httpRes);
            var html = template.Result;

			var htmlBytes = html.ToUtf8Bytes();

			if (Env.IsMono) {
				//var hasBom = html.Contains((char)65279);
				//TODO: Replace sad hack in Mono replacing the BOM with whitespace:
				for (var i=0; i<htmlBytes.Length-3; i++) {
					if (htmlBytes[i] == 0xEF && htmlBytes[i+1] == 0xBB && htmlBytes[i+2] == 0xBF) {
						htmlBytes[i] = (byte)' ';
						htmlBytes[i+1] = (byte)' ';
						htmlBytes[i+2] = (byte)' ';
					}
				}
			}

			httpRes.OutputStream.Write(htmlBytes, 0, htmlBytes.Length);

            var disposable = template as IDisposable;
            if (disposable != null)
            {
                disposable.Dispose();
            }
            httpRes.EndServiceStackRequest(skipHeaders: true);

            return true;
        }

        public void ReloadModifiedPageAndTemplates(ViewPageRef razorPage)
        {
            if (razorPage.FilePath == null) return;

            var lastWriteTime = File.GetLastWriteTime(razorPage.FilePath);
            if (lastWriteTime > razorPage.LastModified)
            {
                razorPage.Reload();
            }

            ViewPageRef template;
            if (razorPage.DirectiveTemplatePath != null
                && this.MasterPageTemplates.TryGetValue(razorPage.DirectiveTemplatePath, out template))
            {
                lastWriteTime = File.GetLastWriteTime(razorPage.DirectiveTemplatePath);
                if (lastWriteTime > template.LastModified)
                    ReloadTemplate(template);
            }
            if (razorPage.TemplatePath != null
                && this.MasterPageTemplates.TryGetValue(razorPage.TemplatePath, out template))
            {
                lastWriteTime = File.GetLastWriteTime(razorPage.TemplatePath);
                if (lastWriteTime > template.LastModified)
                    ReloadTemplate(template);
            }
        }

        private void ReloadTemplate(ViewPageRef template)
        {
            var contents = File.ReadAllText(template.FilePath);
            foreach (var markdownReplaceToken in ReplaceTokens)
            {
                contents = contents.Replace(markdownReplaceToken.Key, markdownReplaceToken.Value);
            }
            template.Reload(contents);
        }

        private ViewPageRef GetViewPageByResponse(object dto, IHttpRequest httpRequest)
        {
            var httpResult = dto as IHttpResult;
            if (httpResult != null)
            {
                //If TemplateName was specified don't look for anything else.
                if (httpResult.TemplateName != null)
                    return GetViewPage(httpResult.TemplateName);

                dto = httpResult.Response;
            }
            if (dto != null)
            {
                var responseTypeName = dto.GetType().Name;
                var markdownPage = GetViewPage(responseTypeName);
                if (markdownPage != null) return markdownPage;
            }

            return httpRequest != null ? GetViewPage(httpRequest.OperationName) : null;
        }

        public ViewPageRef GetViewPage(string pageName)
        {
            ViewPageRef razorPage;

            ViewPages.TryGetValue(pageName, out razorPage);
            if (razorPage != null) return razorPage;

            ViewSharedPages.TryGetValue(pageName, out razorPage);
            return razorPage;
        }

        private void RegisterRazorPages(string razorSearchPath)
        {
            foreach (var page in FindRazorPagesFn(razorSearchPath))
            {
                AddPage(page);
            }
        }

        public void AddPage(ViewPageRef page)
        {
            try
            {
                page.Prepare();
                AddViewPage(page);
            }
			catch (TemplateCompilationException tcex) 
			{
				"Error compiling page {0}".Fmt(page.Name).Print();
				tcex.Errors.PrintDump();
			}
            catch (Exception ex)
            {
                var errorViewPage = new ErrorViewPage(this, ex) {
                    Name = page.Name,
                    PageType = page.PageType,
                    FilePath = page.FilePath,
                };
                errorViewPage.Prepare();
                AddViewPage(errorViewPage);
                Log.Error("Razor AddViewPage() page.Prepare(): " + ex.Message, ex);
            }

            var templatePath = page.TemplatePath;
            if (page.TemplatePath == null) return;

            if (MasterPageTemplates.ContainsKey(templatePath)) return;

            AddTemplate(templatePath, File.ReadAllText(templatePath));
        }

        private void AddViewPage(ViewPageRef page)
        {
            switch (page.PageType)
            {
                case RazorPageType.ViewPage:
                    ViewPages.Add(page.Name, page);
                    break;
                case RazorPageType.SharedViewPage:
                    ViewSharedPages.Add(page.Name, page);
                    break;
                case RazorPageType.ContentPage:
                    ContentPages.Add(page.FilePath.WithoutExtension(), page);
                    break;
            }
        }

        public ViewPageRef AddTemplate(string templatePath, string templateContents)
        {
            var templateFile = new FileInfo(templatePath);
            var templateName = templateFile.FullName.WithoutExtension();

			TemplateService templateService; 
			if (!templateServices.TryGetValue(templateFile.Extension, out templateService))
				throw new ConfigurationErrorsException(
					"No BaseType registered with extension " + templateFile.Extension + " for template " + templateFile.Name);

            foreach (var markdownReplaceToken in ReplaceTokens)
            {
                templateContents = templateContents.Replace(markdownReplaceToken.Key, markdownReplaceToken.Value);
            }

            var template = new ViewPageRef(this, templatePath, templateName, templateContents, RazorPageType.Template) {
                LastModified = templateFile.LastWriteTime,
				Service = templateService,
            };
            MasterPageTemplates.Add(templatePath, template);
            try
            {
                template.Prepare();
                return template;
            }
            catch (Exception ex)
            {
                Log.Error("AddViewPage() template.Prepare(): " + ex.Message, ex);
                return null;
            }
        }

        public ViewPageRef GetContentPage(string pageFilePath)
        {
            ViewPageRef razorPage;
            ContentPages.TryGetValue(pageFilePath, out razorPage);
            return razorPage;
        }

        static readonly char[] DirSeps = new[] { '\\', '/' };
        public ViewPageRef GetContentResourcePage(string pathInfo)
        {
            ViewPageRef razorPage;
            ContentPages.TryGetValue(pathInfo.TrimStart(DirSeps), out razorPage);
            return razorPage;
        }

        public string GetTemplate(string name)
        {
            ViewPageRef template;
            MasterPageTemplates.TryGetValue(name, out template); //e.g. /NoModelNoController.cshtml
            return template != null ? template.Contents : null;
        }

        public ITemplate CreateInstance(Type type)
        {
            var instance = type.CreateInstance();

            var templatePage = instance as ITemplatePage;
            if (templatePage != null)
            {
                templatePage.AppHost = AppHost;
            }

            var template = (ITemplate)instance;
            return template;
        }

        public string RenderStaticPage(string filePath)
        {
            if (filePath == null)
                throw new ArgumentNullException("filePath");

            filePath = filePath.WithoutExtension();

            ViewPageRef razorPage;
            if (!ContentPages.TryGetValue(filePath, out razorPage))
                throw new InvalidDataException(ErrorPageNotFound.FormatWith(filePath));

            return RenderStaticPage(razorPage);
        }

        private string RenderStaticPage(ViewPageRef markdownPage)
        {
            var template = ExecuteTemplate((object)null,
                markdownPage.PageName, markdownPage.TemplatePath);

            return template.Result;
        }

        public IRazorTemplate ExecuteTemplate<T>(T model, string name, string templatePath)
        {
            return ExecuteTemplate(model, name, templatePath, null, null);
        }

		public TemplateService GetTemplateService(string pagePathOrName)
		{
			foreach (var templateService in templateServicesArray)
			{
				if (TemplateService.ContainsPagePath(pagePathOrName))
					return templateService;
			}

			foreach (var templateService in templateServicesArray)
			{
				if (TemplateService.ContainsPageName(pagePathOrName))
					return templateService;
			}

			return null;
		}

        public IRazorTemplate ExecuteTemplate<T>(T model, string name, string templatePath, IHttpRequest httpReq, IHttpResponse httpRes)
        {
			return GetTemplateService(name).ExecuteTemplate(model, name, templatePath, httpReq, httpRes);
        }

        public string RenderPartial(string pageName, object model, bool renderHtml)
        {
            //Razor writes partial to static StringBuilder so don't return or it will write x2
			GetTemplateService(pageName).RenderPartial(model, pageName);
            //return template.Result;
            return null;
        }
    }
}