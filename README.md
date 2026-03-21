# Train Simulator (see build instructions at the bottom)

Latest Documentation Video:

[https://youtu.be/kW29mfmC0UA](url) - Documentation #1
(Documentation video is 1 version behind)



Built on UnityXRT - Intended for use with a VR headset

Current features:



* Cool train model
* You can place and modify tracks for trains to run on
* A train will go down a track at configurable intervals and speed
* There is a timer visible on the track showing how long until the next train



* use TrainArive() to trigger a train passing (currently called when the train timer hits 0)
* use CheckTrainLeft() to identify when a train has finished passing (currently also resets the timer)



* grassy field and shrubbery
* two trains with offset timings
* flashing indicator when train passing




\\/   OPEN / BUILD instructions   \\/

(NOTE: this project uses an older version of Unity for TOS reasons, do not commit with a more recent build of Unity)
Install Unity 2021.3.19f1 for your OS: [https://unity.com/releases/editor/whats-new/2021.3.19f1](url)

Insure you select the correct Build dependencies for your platform:
(For windows, get Windows Build Support, IL2CPP, Windows IL2CPP toolchain) (you can ignore if you don't need to build or you already have them)

Download the project folder or use git fetch
Run Unity -> Open Project -> select the project for this github

You can click the play button in the top center to test with VR equipment or observe the SCENE or GAME views (top left)

To build, press ctrl+b and build the project (if you plan on pushing to this repo, put the exe in a seperate folder or an ignored folder like "build")
