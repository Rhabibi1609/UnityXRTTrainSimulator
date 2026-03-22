# Train Simulator (see build instructions at the bottom)

Latest Documentation Video:

NO DOCUMENTATION VIDEO ON CURRENT BRANCH
(Latest documentation video is on Unity\_stable branch and is 2 versions behind)



Built on UnityXRT - Intended for use with a VR headset
Unity version: 6000.3.11f1

Current features:



* Updated to newer Unity Versions!



* New CC0 train and asset models
* You can place and modify tracks for trains to run on
* A train will go down a track at configurable intervals and speed
* There is a timer visible on the track showing how long until the next train



* use TrainArive() to trigger a train passing (currently called when the train timer hits 0)
* use CheckTrainLeft() to identify when a train has finished passing (currently also resets the timer)



* 3d grassy field and terrain
* Train stations
* two trains with offset timings
* flashing indicator when train passing





\\/   OPEN / BUILD instructions   \\/

(NOTE: this branch uses a newer version of Unity and requires a license if revenue exceeds $100,000)
Install Unity 6000.3.11f1 for your OS: [https://unity.com/releases/editor/whats-new/6000.3.11f1](url)

Insure you select the correct Build dependencies for your platform:
(For windows, get Windows Build Support, IL2CPP, Windows IL2CPP toolchain) (you can ignore if you don't need to build or you already have them)

Download the project folder or use git fetch
Run Unity -> Open Project -> select the project for this GitHub

You can click the play button in the top center to test with VR equipment or observe the SCENE or GAME views (top left)

(Make sure you have the Demo Scene open when testing: bottom right -> Scenes -> "VRTrainSimulator")

To build, press ctrl+b and build the project (if you plan on pushing to this repo, put the exe in a separate folder or an ignored folder like "build")

