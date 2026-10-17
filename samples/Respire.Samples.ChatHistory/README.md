# Redis chat history sample

Start a Redis server, then run:

```sh
dotnet run --project samples/Respire.Samples.ChatHistory -f net10.0 -- redis://localhost:6379
```

The sample stores two turns across separate chat pipelines and verifies that the second
pipeline remembers the first turn. The deterministic demo provider needs no API key;
replace it with any stateless `IChatClient` provider for a real model. The sample clears
its unique conversation on exit.
