using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops;

public enum CustomLoopControlKind
{
    Unknown = 0,
    Pause = 1,
    Cancel = 2,
    Resume = 3
}
