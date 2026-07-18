using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops;

public enum CustomLoopControlOperationState
{
    Unknown = 0,
    Pending = 1,
    Complete = 2
}
