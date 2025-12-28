# ASTRO-NEER (LunaBot)
### Autonomous Swarm Navigation for Lunar Habitat Monitoring

ASTRO-NEER is a simulation-first robotics project that explores how a **swarm of autonomous rovers** can navigate, monitor, and maintain lunar habitats.  
Instead of relying on a single complex robot, the system takes inspiration from **ant colonies in nature** — many simple agents working together to produce intelligent, resilient behavior.

The project focuses on decentralized decision-making, fault tolerance, and real-time evaluation using a Digital Twin, all demonstrated inside a Unity-based simulation environment.

---

## Project Motivation

Lunar habitats operate in environments that are:
- GPS-denied  
- Highly unpredictable  
- Unsafe for constant human presence  

ASTRO-NEER investigates how autonomous rover swarms can:
- Reduce astronaut workload  
- Improve safety through redundancy  
- Continue operating despite individual robot failures  
- Provide continuous situational awareness through live telemetry  

---

## Inspiration from Nature: Ant Colonies

This project is strongly inspired by how **real ant colonies** function.

### A real-world example
When ants search for food:
- Individual ants explore randomly  
- Successful paths are reinforced through pheromone trails  
- Inefficient paths gradually disappear  
- No ant acts as a leader, yet the colony converges on the best route  

### How this translates to ASTRO-NEER
| Ant Colony Behavior | ASTRO-NEER Implementation |
|--------------------|---------------------------|
| Random exploration | Independent rover scouting |
| Pheromone trails | Shared path quality feedback |
| Trail evaporation | Time-decayed path confidence |
| No central leader | Decentralized swarm logic |
| Redundancy | Mission survives rover failures |

This design makes the system scalable, adaptive, and resilient — all essential traits for space exploration.

---

## Key Features

- Autonomous rover navigation in a simulated lunar environment  
- Decentralized swarm coordination with no single point of control  
- Vision-based perception and terrain awareness  
- Environment-driven path selection inspired by stigmergy  
- Digital Twin for live telemetry and evaluation  
- Unity-based simulation for realistic visualization  
- Modular design allowing independent upgrades to perception, planning, or coordination  

---

## System Overview

```

Sensors → Perception → Local Mapping → Path Planning → Rover Control
↘
Swarm Coordination (shared state + feedback)
↘
Telemetry → Digital Twin → Evaluation

```

### Design principles
- Each rover makes its own decisions using local information  
- Only lightweight state data is shared between rovers  
- No central controller is required  
- Simulation-first approach enables fast and safe iteration  

---

## Technology Stack

- **Languages**: Python, C# (Unity)
- **Simulation**: Unity
- **Perception**: OpenCV, ML-based vision models
- **Swarm Logic**: Custom decentralized coordination
- **Telemetry & Evaluation**: Python backend + dashboard
- **Environment Management**: Python virtual environment

> Note: This project does not use ROS or ROS2.

---

## Repository Structure (High Level)

```

├── setup.bat
├── requirements.txt
├── Assets/                # Unity assets (scenes, models, prefabs)
├── Scripts/               # Rover logic and swarm behavior
├── Python/                # Telemetry, evaluation, backend logic
├── Dashboard/             # Digital Twin visualization
├── Docs/                  # Slides, references, diagrams
└── README.md
```


---

## How to Run the Project

Follow the steps below to run the simulation smoothly.

---

### Step 1: Environment Setup (One-Time)

Run the setup script from the repository root:

```

setup.bat

```

This script:
- Creates a Python virtual environment  
- Installs all required dependencies  
- Prepares the backend components for telemetry and evaluation  

Once the script finishes successfully, no further setup is required.

---

### Step 2: Open the Project in Unity

1. Open **Unity Hub**  
2. Click **Open Project**  
3. Select the repository root folder  
4. Allow Unity to import all assets (this may take a moment on first launch)

For best results, use a Unity LTS version (2021.3 or newer).

---

### Step 3: Switch to Evaluation Mode

Inside Unity:
- Open the **Evaluation Scene**, or  
- Select **Evaluation Mode** using the project’s mode selector

Evaluation mode enables consistent test conditions, metrics collection, and telemetry output.

---

### Step 4: Add the Rover to the Scene

1. Locate the **Rover** prefab or model in the Assets panel  
2. Drag and drop it into the Scene or Hierarchy  
3. Place it on the terrain surface  

Ensure that all required components are enabled in the Inspector.

---

### Step 5: Run the Simulation

Press **Play** in the Unity editor.

You should observe:
- The rover beginning autonomous navigation  
- Swarm behavior activating when multiple rovers are present  
- Telemetry being generated for evaluation and visualization  

If the Digital Twin or dashboard is part of the run, ensure the backend service is active.

---

## Digital Twin & Evaluation

The Digital Twin provides:
- Live rover position and system status  
- Sensor and health monitoring  
- Replayable evaluation runs  
- Clear visibility into swarm-level behavior  

This makes the project suitable for demonstrations, analysis, and iterative improvement.

---

## Testing and Evaluation Goals

- Navigation stability and accuracy  
- Area coverage efficiency  
- Quality of swarm coordination  
- System behavior under rover failure  
- Communication and computation overhead  

---

## Future Improvements

- Advanced task allocation strategies  
- Reinforcement learning for adaptive path planning  
- Improved terrain classification models  
- Larger multi-habitat simulations  
- Quantitative benchmarking across swarm sizes  

---

## Team and Credits

Developed by the **ASTRO-NEER team** as part of PEC 3.0.  
Contributions span perception, simulation, swarm logic, testing, and evaluation.

---

## License

This project is licensed under the **GNU General Public License v3.0 (GPL-3.0)**.\
See the `LICENSE` file for more details.

---


## Final Note

ASTRO-NEER is not about a single intelligent robot —  
it is about how **simple agents, when designed carefully, can work together to solve complex problems**.

Explore the simulation, observe the swarm, and see emergent intelligence in action.

---
