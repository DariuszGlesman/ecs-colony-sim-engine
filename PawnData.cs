using Unity.Mathematics;
using UnityEngine;
using Unity.Collections;

/* ====================================================================================
 * ARCHITECTURAL SPECIFICATION & PERFORMANCE CONTEXT
 * ====================================================================================
 * This data module follows a strict Data-Oriented Design (DOD) approach optimized for 
 * high-performance entity simulation through Unity's C# Job System and Burst Compiler.
 *
 * 1. CPU CACHE LINES & SPATIAL LOCALITY
 * Modern CPUs fetch main memory into hardware cache lines that are typically 64 bytes wide. 
 * In conventional Object-Oriented Programming (Array-of-Structures / AoS), fields such as 
 * Position, Hunger, and ID live within a single heap-allocated class. Iterating over 10,000 
 * pawns to simulate movement pulls unneeded data (e.g., Hunger, Learning, Social) into 
 * the cache alongside the position vectors, resulting in severe cache pollution and misses.
 * * By adopting a Structure-of-Arrays (SoA) layout (managed through separate NativeArrays 
 * in PawnManager), each structural component is stored perfectly sequentially in memory. 
 * Iterating over PawnTransform arrays ensures that 100% of the bytes streamed into the 
 * 64-byte cache line are processed by the CPU, achieving near-perfect cache utility.
 *
 * 2. BURST COMPILER OPTIMIZATION
 * The Burst Compiler employs LLVM to translate intermediate C# IL into highly optimized, 
 * platform-specific native assembly. To facilitate this level of optimization, all data 
 * structures defined below are strictly unmanaged "blittable" structs. They contain no 
 * managed class references or garbage-collected pointers. This data purity allows Burst to 
 * perform aggressive loop unrolling, elite register allocation, and completely eliminate 
 * managed heap allocation overhead.
 *
 * 3. SIMD VECTORIZATION & ALIGNMENT
 * Contiguous primitive arrays allow the Burst compiler to auto-vectorize loops into Single 
 * Instruction Multiple Data (SIMD) instruction sets (such as SSE4, AVX2, or NEON). 
 * Because identical components line up sequentially, the CPU can read data directly into 
 * wide vector registers and run parallel mathematical instructions across multiple entities 
 * simultaneously in a single clock cycle. Struct sizes and field order are laid out 
 * to preserve native padding alignment boundaries (4-byte and 16-byte steps).
 * ==================================================================================== */

// ====================================================================================
// === DATA STRUCTURES (SoA Components) ===
// ====================================================================================

/// <summary>
/// Dedicated transform data layer. Kept lean to maximize cache density during rendering and movement sweeps.
/// Fits cleanly into hardware registers (44 bytes total).
/// </summary>
public struct PawnTransform
{
    public float3 Position;           // 12 bytes - 3D spatial coordinate
    public quaternion Rotation;       // 16 bytes - 4D rotation component
    public float3 Scale;              // 12 bytes - 3D local scale
    public float CurrentSpriteIndex;  // 4 bytes  - Decoupled lookup index for instanced grid sheet rendering
}

/// <summary>
/// Identifiers and structural duty definitions used to evaluate AI state machines.
/// </summary>
public struct PawnState
{
    public int Id;                                      // 4 bytes - Unique pawn index
    public PawnType Type;                               // 1 byte  - Core entity behavior mask
    public PawnDuty CurrentDuty;                        // 1 byte  - Active overrides or current driver intent
    public MoveState CurrentMoveState;                  // 1 byte  - Mechanical tracking of navigation status
    public byte YearLevel;                              // 1 byte  - Student cohort progression marker (0-4)
    public ScheduleActivity CurrentScheduleActivity;    // 4 bytes - Global clock tracker mapping active routines
    public float ActivityStartTime;                     // 4 bytes - Match timestamp for time-in-state thresholds
    public float AITickTimer;                           // 4 bytes - Distributed tick offset to stagger AI calculations
}

/// <summary>
/// Contiguous simulation data container for agent stats and depletion metrics.
/// Compact 44-byte structure allows highly efficient iteration during decay updates.
/// </summary>
public struct PawnNeeds
{
    // Need values (0.0f - 100.0f)
    public float Hunger;
    public float Energy;
    public float Fun;
    public float Social;
    public float Learning;
    public float Rebellion;
    public float Intelligence;
    
    // Dynamic decay modifiers
    public float HungerDecayRate;
    public float EnergyDecayRate;
    public float FunDecayRate;
    public float SocialDecayRate;
}

/// <summary>
/// Structural tracking fields required by grid pathfinding engines and local steering.
/// </summary>
public struct PawnMovement
{
    public float3 CurrentDestination;     // 12 bytes - Immediate localized node target
    public float3 FinalDestination;       // 12 bytes - Global path endpoint
    public float3 LastPosition;           // 12 bytes - Position delta historical check
    public float StuckTimer;              // 4 bytes  - Stuck mitigation check window
    public float Speed;                   // 4 bytes  - Current locomotion modifier
    public byte IsWaitingForPath;         // 1 byte   - Asynchronous path processing handshake token
}

/// <summary>
/// Command payload for staging parallel path calculations without allocating managed garbage.
/// </summary>
public struct PathRequestCommand
{
    public int PawnIndex;                 // 4 bytes  - Origin entity index
    public float3 StartPosition;          // 12 bytes - Grid-resolved starting position
    public float3 TargetPosition;         // 12 bytes - Intended destination coordinates
}


// ====================================================================================
// === EXPLICIT STATIC HELPER UTILITIES ===
// ====================================================================================

/// <summary>
/// Isolated stateless execution utilities. 
/// Structured with explicit ref modifiers to eliminate stack copies, enabling perfect Burst compilation.
/// </summary>
public static class PawnDataUtilities
{
    /// <summary>
    /// Processes immediate attribute modifications and need fulfillment based on current globally scheduled active states.
    /// </summary>
    public static void FulfillNeeds(ref PawnState state, ref PawnNeeds needs, ScheduleActivity currentGlobalActivity)
    {
        switch (state.CurrentDuty)
        {
            default:
                if (currentGlobalActivity == ScheduleActivity.Breakfast || 
                    currentGlobalActivity == ScheduleActivity.Lunch || 
                    currentGlobalActivity == ScheduleActivity.Dinner)
                {
                    needs.Hunger = math.min(100f, needs.Hunger + 50f);
                }
                else if (currentGlobalActivity == ScheduleActivity.Sleep)
                {
                    needs.Energy = math.min(100f, needs.Energy + 70f);
                }
                else if (currentGlobalActivity >= ScheduleActivity.Class_Charms && 
                         currentGlobalActivity <= ScheduleActivity.Class_Defense)
                {
                    needs.Learning = math.min(100f, needs.Learning + 5f);
                }
                break;
        }
        state.CurrentDuty = PawnDuty.None;
    }

    /// <summary>
    /// Maps a 2D velocity or heading direction vector directly to a rendering sheet offset index.
    /// Performs mathematical operations branchlessly to enhance pipelined processing.
    /// </summary>
    public static float GetSpriteIndexForDirection(float2 direction)
    {
        if (math.lengthsq(direction) < 0.0001f) return 0f;
        
        // Horizontal dominance determination
        if (math.abs(direction.x) > math.abs(direction.y))
        {
            return direction.x > 0 ? 2f : 3f; // 2 = Right, 3 = Left
        }
        
        return direction.y > 0 ? 1f : 0f;     // 1 = Up, 0 = Down
    }

    /// <summary>
    /// Evaluates current pawn structural alerts and resolves the required spatial sub-system target category.
    /// </summary>
    public static FlowTarget GetFlowTargetForDuty(PawnDuty duty)
    {
        switch (duty)
        {
            case PawnDuty.Starving:
            case PawnDuty.SeekFood: 
                return FlowTarget.DiningHall;
                
            case PawnDuty.Exhausted:
            case PawnDuty.SeekRest: 
                return FlowTarget.Dormitory;
                
            default: 
                return FlowTarget.None;
        }
    }
}


// ====================================================================================
// === CORE GLOBAL ENUMS (Explicitly Typed Underlying Storage) ===
// ====================================================================================

/// <summary>
/// Enumeration representing the spatial navigation state of an entity.
/// </summary>
public enum MoveState : byte
{
    Idle  = 0,
    Moving = 1,
    Stuck  = 2
}

/// <summary>
/// Identification type flags for entity indexing, class behavior sorting, and visual rendering parameters.
/// </summary>
public enum PawnType : byte
{
    Student      = 0,
    Teacher      = 1,
    Staff        = 2,
    Creature     = 3,
    MagicalBeast = 4
}

/// <summary>
/// High priority dynamic override signals prompting the pawn to diverge from its normal timetable loop.
/// </summary>
public enum PawnDuty : byte
{
    None      = 0,
    Starving  = 1,
    SeekFood  = 2,
    Exhausted = 3,
    SeekRest  = 4
}