namespace EmbodySense.Core.Application.Harness;

public static class AgentHarnessLoop
{
    public static async Task<int> RunHarnessLoopAsync(
        AgentHarnessSession session,
        IHarnessClient client,
        HarnessCommandHandler? commandHandler = null,
        AgentHarnessLoopOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(client);

        options ??= new AgentHarnessLoopOptions();
        commandHandler ??= new HarnessCommandHandler(client);

        if (!string.IsNullOrEmpty(options.Banner))
        {
            client.WriteLine(options.Banner);
        }

        var state = new HarnessLoopState();

        while (!state.ExitRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            client.Write(options.Prompt);
            var input = client.ReadLine();

            switch (input)
            {
                case null:
                    state.RequestExit();
                    break;

                case var value when string.IsNullOrWhiteSpace(value):
                    break;

                default:
                    if (await commandHandler.TryHandleAsync(input, session, state, cancellationToken))
                    {
                        break;
                    }

                    var wroteResponseChunk = false;
                    var responseEndedWithNewLine = false;
                    var response = await session.SendUserMessageAsync(input, (chunk, _) =>
                    {
                        if (!string.IsNullOrEmpty(chunk))
                        {
                            client.Write(chunk);
                            wroteResponseChunk = true;
                            responseEndedWithNewLine = EndsWithNewLine(chunk);
                        }

                        return Task.CompletedTask;
                    }, cancellationToken);

                    if (!wroteResponseChunk)
                    {
                        client.WriteLine(response.OutputText);
                    }
                    else if (!responseEndedWithNewLine)
                    {
                        client.WriteLine();
                    }

                    state.MarkModelTurnStarted();
                    break;
            }
        }

        return 0;
    }

    private static bool EndsWithNewLine(string text)
    {
        return text.Length > 0 && text[^1] is '\n' or '\r';
    }
}
