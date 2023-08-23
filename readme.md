# Distributed tracing

## How to run the app

1) Run the docker compose file in the parent directory by running the below command. It will startup all the depedencies - zipkin, jaeger, rabbitmq and seq.

> docker compose -f docker-compose.dependencies.yml up

2) Open the solution and right click on the solution, configure startup projects. Mark all the projects to run without debugging. This will run web server, web client, gateway, iot and device. Web client will be available at https://localhost:5001,
