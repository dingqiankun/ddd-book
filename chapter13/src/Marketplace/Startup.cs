﻿using System;
using System.Threading.Tasks;
using EventStore.ClientAPI;
using Marketplace.EventSourcing;
using Marketplace.Infrastructure.Currency;
using Marketplace.Infrastructure.EventStore;
using Marketplace.Infrastructure.Profanity;
using Marketplace.Infrastructure.RavenDb;
using Marketplace.Infrastructure.Vue;
using Marketplace.Modules.Auth;
using Marketplace.Modules.ClassifiedAds;
using Marketplace.Modules.Projections;
using Marketplace.Modules.UserProfile;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Swashbuckle.AspNetCore.Swagger;
using IHostingEnvironment = Microsoft.AspNetCore.Hosting.IHostingEnvironment;

// ReSharper disable UnusedMember.Global

[assembly: ApiController]

namespace Marketplace
{
    public class Startup
    {
        public const string CookieScheme = "MarketplaceScheme";

        public Startup(IHostingEnvironment environment, IConfiguration configuration)
        {
            Environment = environment;
            Configuration = configuration;
        }

        private IConfiguration Configuration { get; }
        private IHostingEnvironment Environment { get; }

        public void ConfigureServices(IServiceCollection services)
        {
            Modules.ClassifiedAds.EventMappings.MapEventTypes();
            Modules.UserProfile.EventMappings.MapEventTypes();
            
            var esConnection = EventStoreConnection.Create(
                Configuration["eventStore:connectionString"],
                ConnectionSettings.Create().KeepReconnecting(),
                Environment.ApplicationName);
            var store = new EsAggregateStore(esConnection);
            var purgomalumClient = new PurgomalumClient();
            var documentStore = ConfigureRavenDb(
                Configuration["ravenDb:server"],
                Configuration["ravenDb:database"]);

            Func<IAsyncDocumentSession> getSession = () => documentStore.OpenAsyncSession();

            services.AddSingleton(
                new ClassifiedAdsCommandService(store, new FixedCurrencyLookup()));
            services.AddSingleton(
                new UserProfileCommandService(store, t => purgomalumClient.CheckForProfanity(t)));

            var ravenDbProjectionManager = new ProjectionManager(esConnection,
                new RavenDbCheckpointStore(getSession, "readmodels"),
                ConfigureRavenDbProjections(esConnection, getSession)
            );
            var upcasterProjectionManager = new ProjectionManager(esConnection,
                new EsCheckpointStore(esConnection, "upcaster"),
                ConfigureRavenDbProjections(esConnection, getSession)
            );

            services.AddSingleton(c => getSession);
            services.AddSingleton(c => new AuthService(getSession));
            services.AddScoped(c => getSession());
            services.AddSingleton<IHostedService>(
                new EventStoreService(
                    esConnection, 
                    ravenDbProjectionManager,
                    upcasterProjectionManager));

            services
                .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie();

            services
                .AddMvcCore()
                .AddJsonFormatters()
                .AddApiExplorer()
                .SetCompatibilityVersion(CompatibilityVersion.Version_2_2);

            services.AddSpaStaticFiles(configuration => 
                configuration.RootPath = "ClientApp/dist");
            services.AddSwaggerGen(c => 
                c.SwaggerDoc("v1", 
                    new Info
                    {
                        Title = "ClassifiedAds", Version = "v1"
                    }));
        }

        public void Configure(IApplicationBuilder app, IHostingEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseAuthentication();
            app.UseMvc(routes =>
            {
                routes.MapRoute(
                    name: "default",
                    template: "{controller=Home}/{action=Index}/{id?}");
                routes.MapRoute(
                    name: "api",
                    template: "api/{controller=Home}/{action=Index}/{id?}");
            });

            app.UseStaticFiles();
            app.UseSpaStaticFiles();

            app.UseSwagger();
            app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "ClassifiedAds v1"));

            app.UseSpa(spa =>
            {
                spa.Options.SourcePath = "ClientApp";

                if (env.IsDevelopment())
                {
//                    spa.UseVueDevelopmentServer("serve:bs");
                }
            });
        }

        private static IDocumentStore ConfigureRavenDb(
            string serverUrl,
            string database)
        {
            var store = new DocumentStore
            {
                Urls = new[] {serverUrl},
                Database = database
            };
            store.Initialize();
            var record = store.Maintenance.Server.Send(
                new GetDatabaseRecordOperation(store.Database));
            if (record == null)
            {
                store.Maintenance.Server.Send(
                    new CreateDatabaseOperation(new DatabaseRecord(store.Database)));
            }

            return store;
        }

        private static IProjection[] ConfigureRavenDbProjections(
            IEventStoreConnection esConnection,
            Func<IAsyncDocumentSession> getSession)
        {
            return new IProjection[]
            {
                new ClassifiedAdDetailsProjection(getSession,
                    async userId => (await GetUserDetails(userId))?.DisplayName),
                new ClassifiedAdUpcasters(esConnection,
                    async userId => (await GetUserDetails(userId))?.PhotoUrl),
                new UserDetailsProjection(getSession),
                new MyClassifiedAdsProjection(getSession)
            };

            Task<ReadModels.UserDetails> GetUserDetails(Guid userId)
                => getSession.GetUserDetails(userId);
        }

    }
}