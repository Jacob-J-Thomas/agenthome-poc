using EmbodySense.HumanInputContinuationHost;

return args switch
{
    ["wake", ..] => await HumanInputResponseContinuationHost.RunAsync(args[1..]),
    ["coordinator", ..] => await GovernedLoopCoordinatorHost.RunAsync(args[1..]),
    ["publication", ..] => await HumanInputRequestPublicationHost.RunAsync(args[1..]),
    _ => 2,
};
