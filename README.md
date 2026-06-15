# ecs-colony-sim-engine

# Unity GPU Frustum Culling & ECS Architecture

A high-performance, data-oriented rendering and culling system built for Unity using Entity Component System (ECS) principles and HLSL Compute Shaders.

## 🚀 Overview
This repository demonstrates a custom, production-ready GPU frustum culling implementation. It efficiently processes thousands of dynamic entities ("Pawns") and static grid elements, outputting only visible instances to a rendering stream. 

Designed as a portfolio piece showcasing advanced graphics programming, data-oriented design (DoD), and cross-platform shader compatibility.

## ✨ Key Features
* **Compute Shader Culling:** Offloads bounding-sphere frustum culling entirely to the GPU (`CullPawns` and `CullGrid` kernels).
* **Data-Oriented Structures:** Utilizes Struct of Arrays (SoA) for dynamic Pawns (`PawnData.cs`) and Array of Structs (AoS) for the static grid, optimizing CPU/GPU memory access patterns.
* **Cross-API Matrix Stability:** Implements explicit matrix translation extraction to prevent row-major/column-major inversion bugs when cross-compiling Unity's C# structural format across varying graphics APIs (DirectX, Vulkan, Metal).
* **Optimized Branching:** Uses explicit loop unrolling (`[unroll]`) in HLSL to eliminate branch overhead during frustum plane checks.

## 🧠 Architecture Highlights

### `PawnData.cs` (Struct of Arrays)
Contains the ECS structural definitions for entities, utilizing SoA layouts to ensure CPU cache coherency during systemic logic updates. Includes helper methods for maintaining state.

### Compute Shader Logic
The culling system reads transformation matrices and sprite indices from structured buffers, performs mathematical visibility checks against the camera's 6 frustum planes, and dynamically appends visible items to a shared output buffer (`AppendStructuredBuffer<InstanceData>`) to be passed directly to `Graphics.DrawMeshInstancedIndirect`.

## 🛠 Getting Started

### Prerequisites
* Unity (2022.3 LTS or newer recommended)
* A target platform that supports Compute Shaders (DirectX 11/12, Vulkan, Metal)

🤝 Contributing
As this is primarily a portfolio piece, direct contributions are not actively expected, but feedback, forks, and pull requests regarding optimization are always welcome!

📝 License
This project is open-source and available under the MIT License.