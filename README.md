## The Transfer Broker is a replacement for the vanilla Transfer Manager.
Its functions:

 - Matching supply and demand is done based on network distance, and traffic conditions, unlike vanilla, which matches based on straight line proximity, urgency and a pseudo-random building ID (see [url=https://steamcommunity.com/app/255710/discussions/0/3014563919018630110/#c3115895544610249746]this thread[/url]).
 - Difficulty mode is configurable, from making the best matches possible (easy mode), to deliberately making poor matches (hard mode)
 - It is possible to lock down all match making, as a test (eg, to find the longest supply lines)
 - Vehicles that contribute the most to traffic congestion are color-coded on the [b]Traffic Routes[/b] View (important: not the [b]Traffic[/b] view).
 - Inter-warehouse transfers vastly improved.

### Warning
Reading the information on this page in its entirety is **strongly recommended** before subscribing to it. The configuration of the Transfer Broker works differently than other mods.

## Operating Modes
The operation of the Transfer Broker is governed by the presence of some buildings in the city, and their custom names. Depending on which buildings are present, and what they are named, one of the following operating modes are activated:

### Default Mode
When the Brokerage Offices are not installed, transfers are matched with the same algorithm as the vanilla game. The only benefit is that match-making will be multi-threaded, and removed from the Simulation thread. So simulation may be modestly faster (a couple of percentage points). Two Matchmaker threads are used (it may change after BETA).

**This mod requires the "[Ability to Read](https://steamcommunity.com/sharedfiles/filedetails/?id=1145223801)" asset**.

### Basic Activated Mode
In this mode, the brokerage uses an **[All Nearest Neighbours](url=http://renata.borovica-gajic.com/data/DASFAA2018_ANN.pdf)** search based on the road network. The cost function is travel time, that is to say the search will connect transfer partners connected by the fastest routes. Congestion is not taken into account in this mode.

To enable Basic Activated Mode, you must build the Transport Tower monument building. Its construction cost and upkeep have been modestly increased to pay for the necessary equipment and highly-skilled work force needed to run your city's traffic.

This mode should prove to be a vast improvement over the vanilla match-making, especially for non-grid cities. If your road connectivity is constrained by mountains, rivers, oceans, or is made up of several disconnected suburbs, you need a capable Transfer Broker to handle your transportation affairs.

It's recommended that you start in this mode.

### Easy Activated Mode
If you find that Basic Activated Mode seems blind to existing congestion hot-spots, you can install traffic cameras. The Traffic Brokerage is able to make use of the live video feeds by trying to avoid matching through traffic jams, if at all possible. The highly trained brokers will match partners around congested areas, even if transit time will be a little longer.

To install the traffic cameras, you must place a Police Station in your city, and name it "Traffic Operations Center".

### Challenge Mode
If you are interested in this mod because of the bugs within the naive implementation of the vanilla Transfer Manager, and not because you're looking for an easy fix to your city's dysfunctional traffic, you will find the Traffic Broker's algorithm much more deterministic and well-behaved. If you're looking for a way to stress your city with deliberately inefficient match-making, the challenge mode is for you. You'll find reading the mod source code of significant assistance too. A detailed description appears in the function [i]MatchMaker.WarpedUncongestedTravelTime()[/i]. You'll also need some trigonometry, although using a calculator would be overkill.

The Challenge mode is activated when the Traffic Tower is renamed to a more appropriate name which ends with a decimal number, between 1.0 and 4.0 inclusively. This figure is the angle in the polar coordinate of an operating point residing on a unity circle centered at (0.5, 0.5) in cartesian space. Simply convert the desired operating point from cartesian to polar coordinates to find the right figure to use in your Brokerage Firm's name.

The challenge cost function used to find the nearest neighbours uses two parameters, x and y, which warp the length of each segment in the network. It's as if the map is suddenly hilly. The pair (x, y) is the the operating point of your brokerage.

The magnitude of x determines the frequency with which segments in the road network are warped, and the magnitude of y determines the range of random lengthening that is applied to each individual segment. The value of x is used as seed in the RNG, so even a minute change in x will result in an entirely different bump-map. This arrangement means there is a small infinity of operating points for you to try.

Additionally, the magnitude of y also determines the amount of discount in the Brokerage's upkeep. After all, it's only fair that the point of maximum chaos also results in the maximum discount. That discount will be deducted from the brokerage workers's wages.

The challenge to you is, can you find an operating point where traffic is not complete chaos, and brokerage upkeep is also reasonable? Maybe you can have your pi and eat it, too.

### Lockdown Mode

Well, dear mayor, it's time to assert your power over your city. You can, with a flick of a switch, lock down your city's transportation if you consider it will save it from certain doom.

Turning off the Brokerage will cause all match-making work to stop immediately. Matches which have already been made will be routed to their destinations, but no new matches will be made, at least until you achieve the results you were hoping for, or are ready to face the music, whichever comes first.

## Incompatible Mods##
There is a [collection](url=https://steamcommunity.com/sharedfiles/filedetails/?id=2390864898) tracking compatible, incompatible and mods with unclear status ("feedback-requested").

Generally mods that disable some core game mechanic are likely to be incompatible (turn off crime, death, exports, education, etc)
A non-exhaustive list is:


 - Geli-Districts
 - Enhanced Garbage Hearse AI
 - Enhanced Garbage Truck AI
 - Enchnced
 - SOM - Services Optimisation Module
 - More Effective Transfer Manager (TB overrides METM, and remains active. METM will be ineffectual)

## Removing the mod

The mod does not change the game save in any way, and can be removed or added at any time, including in the middle of a game. It's not necessary to exit to desktop after installing or removing it (although other mods might require this)
