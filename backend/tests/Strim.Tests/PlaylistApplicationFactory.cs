using System;
using System.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Strim.Api.Data;

namespace Strim.Tests;

public class PlaylistApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            var dbContextDescriptors = services.Where(d => d.ServiceType == typeof(DbContextOptions<StrimDbContext>)).ToList();
            foreach (var descriptor in dbContextDescriptors)
            {
                services.Remove(descriptor);
            }

            var factoryDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IDbContextFactory<StrimDbContext>));
            if (factoryDescriptor is not null)
            {
                services.Remove(factoryDescriptor);
            }

            services.AddDbContext<StrimDbContext>(options => options.UseInMemoryDatabase($"strim-tests-{Guid.NewGuid()}"));

            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<StrimDbContext>();
            db.Database.EnsureCreated();
        });
    }
}
