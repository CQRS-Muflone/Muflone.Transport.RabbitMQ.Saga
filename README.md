# Muflone Transport RabbitMQ Saga
Muflone extension to manage sagas on RabbitMQ. 
 
### Install ###
`Install-Package Muflone.Transport.RabbitMQ.Saga`


### Implementation ###
To implement a saga register

    public static IServiceCollection AddRabbitMq(this IServiceCollection services, RabbitMqSettings rabbitMqSettings)
	{
		var serviceProvider = services.BuildServiceProvider();
		var repository = serviceProvider.GetRequiredService<IRepository>();
		var loggerFactory = serviceProvider.GetService<ILoggerFactory>();

		var rabbitMQConfiguration = new RabbitMQConfiguration(rabbitMqSettings.Host, rabbitMqSettings.Username, rabbitMqSettings.Password, rabbitMqSettings.ExchangeCommandName, rabbitMqSettings.ExchangeEventName);
		var connectionFactory = new MufloneConnectionFactory(rabbitMQConfiguration, loggerFactory!);

		services.AddMufloneTransportRabbitMQ(loggerFactory, rabbitMQConfiguration);

		serviceProvider = services.BuildServiceProvider();
		services.AddMufloneRabbitMQConsumers(new List<IConsumer>
		{
			new BeersReceivedConsumer(serviceProvider.GetRequiredService<IServiceBus>(),
				connectionFactory,
				loggerFactory),

			new CreateBeerConsumer(repository!, connectionFactory,
				loggerFactory),

			new BeerCreatedConsumer(serviceProvider.GetRequiredService<IBeerService>(),
				connectionFactory,
				loggerFactory),

			new LoadBeerInStockConsumer(repository!, connectionFactory,
				loggerFactory),

			new BeerLoadedInStockConsumer(serviceProvider.GetRequiredService<IBeerService>(),
				connectionFactory,
				loggerFactory),

			new StartBeersReceivedSagaConsumer(serviceProvider.GetRequiredService<IServiceBus>(),
				serviceProvider.GetRequiredService<ISagaRepository>(),
				repository!,
				connectionFactory,
				loggerFactory),

			new BeerCreatedSagaConsumer(serviceProvider.GetRequiredService<IServiceBus>(),
				serviceProvider.GetRequiredService<ISagaRepository>(),
				connectionFactory,
				loggerFactory),

			new BeerLoadedInStockSagaConsumer(serviceProvider.GetRequiredService<IServiceBus>(),
				serviceProvider.GetRequiredService<ISagaRepository>(),
				connectionFactory,
				loggerFactory)
		});

		return services;
	}


### Fully working example
You can find a fully working example here: https://github.com/BrewUp/