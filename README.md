# OpenSim Gloebit Money Module
This is a plugin (addon) to enable the Gloebit currency service on an OpenSim grid.  It also serves as an example which can be referenced or ported to integrate the Gloebit service with other platforms.

# How to use this with OpenSim
1. Download or Build the DLL
  * Download\
    If you don't want to build yourself, you can download the most recent release of the plugin [here](http://dev.gloebit.com/opensim/downloads/).  Download the DLL built for or as close to your version of OpenSim as possible.  If you run into any linking errors, then either the version of OpenSim or your build environment are incompatible with the prebuilt DLLs and you'll need to build it directly against your repository.
  * or Build\
    For the latest features and to ensure compatibility with your system, we recommend building the DLL yourself.
    1. Clone or Download this repository
    2. Copy the Gloebit directory into the addon-modules directory in your OpenSim repository
    3. Run the OpenSim runprebuild script eg:`. runprebuild.sh`
    4. Build OpenSim eb:`xbuild`
2. Configure the plugin
  * Follow the instructions [here](http://dev.gloebit.com/opensim/configuration-instructions/).

# Understanding, Contributing to, and Porting this Plugin

## Code Organization - The breakdown of the functional layers

Starting with the foundation...

### The REST / Web API Layer
* GloebitAPI.cs

This layer provides a C# interface for connecting with Gloebit's web service apis through defined endpoints.

This layer is platform agnostic and shouldn't need modification for porting to another C# platform.  However, it does use an older asyncronous C# pattern compatible with earlier versions of mono.  If another C# platform did not have those compatibility issues, an alternate version could be written to take advantage of the newer Async and Await calls, though it is unknown if there are any performance gains that would be worth this effort.  It also serves as example code for anyone who needs to build a web api for connecting to gloebit in a language other than C#.

Modifications to this layer should really be intended to be universal improvements.  Some such improvements on our list are to...
* convert OSDMaps to Dictionaries and UUIDs to GUIDs so we can remove the requirement for OpenMetaverse libraries to make this more generic.
* implement new endpoints as Gloebit makes them available
* separate GloebitAPI.cs into a true web API (using forms rather than object classes) wrapped in an object API which provides an object interface and converts to and from the form interface of the web api.
* implement a better error reporting system making use of the exception classes used to create the errors by Gloebit.

### The Object Layer
* GloebitUser.cs
* GloebitTransaction.cs
* GloebitSubscription.cs

This layer provides C# classes for more easily managing the data passed to and from the Gloebit API and used throughout the module.

This layer is platform agnostic and shouldn't need modification for porting to another C# platform.

Modifications to this layer would be necessary if there was additional data you wanted to package with the objects or store in database tables.  Some such improvements on our list are to...
* convert UUIDs to GUIDs or strings to remove the requirement for the OpenMetaverse library.
* remove the parameters specific to a BuyObject transaction in OpenSim from GloebitTransaction and move them either to an object sale asset class with permanent storage or to an object sale asset map in local memory.  Will need to keep an asset type and asset id field for asset retrieval and possibly for enacting simple assets which don't need to store more data requiring an asset class or map.
* add asset classes or maps for other transaction types as necessary which are currently making use of the BuyObject fields in GloebitTransaction and cannot be handled generically.
* add the description string to GloebitTransaction
* add data to GloebitTransaction as requested by some customers such as the region ID and region name, or if particular to OpenSim, perhaps store them in a separate object/table.
* add user name to the GloebitUser object so that it doesn't have to be passed separately into functions that might call authorize, such as GetAgentBalance.  Would likely require some new api endpoints to create and update an AppUser and a flow change to the GMM so that AppUsers are created (with extra info such as name and image, etc) prior to attempting to call functions on a user.

### The Database Interface Layer
* GloebitUserData.cs
* GloebitTransactionData.cs
* GloebitSubscriptionData.cs
* Resources/GloebitUsers*.migrations
* Resources/GloebitTransactions*.migrations
* Resources/GloebitSubscriptions*.migrations

This layer handles the storage and retrieval of objects to and from a database.

This layer likely needs modification for porting to another platform.  The Data classes are built upon GenericTableHandlers provided by OpenSim and the migration format may be specific to OpenSim.  These classes should be modified to work with the database interface of the intended platform so that the storage and retrieval calls from the object classes work as expected.

Integrating a database other than MySql, PGSQL or SQLite would be done here by adding the implementation to each data class and adding a migration for each class to the resources.

### The Functional Helper Layer
* GloebitAPIWrapper.cs

This layer wraps the API in a more useful functional layer, converts platform specific information into formats expected by the API and vice versa, handles callbacks from the api, http callbacks from the Gloebit service, and errors, and manages as much processing logic as should be generic to all platforms.  This layer should drastically simplify integration, and will likely need to evolve as new platforms integrate the plugin.  It defines some interfaces which the platform glue layer must implement.  Some of these may at some point be converted to events that can be registered for.

This layer is where some editing will be necessary when porting to another platform.  The signatures of the http callbacks may be specific to the platform and therefor may need adjustment.  In simplifying the returns from the API layer, it is possible we've elimiated information a platform may need, in which case new interface functions may be necessary.  We recommend keeping the platform logic out of this layer in these cases and requesting that we adopt new interface functions or argument passing into the root plugin.

### The Platform Glue Layer
* GloebitMoneyModule.cs

This layer contains all the connections to the larger platform.  It reads configuration, creates the API, registers the http callbacks, defines the platform specific transactions, implements all interfaces necessary for the APIWrapper, and controls the full API flow.  This contains the platform specific logic.  Currently, this file is both the glue between OpenSim and Gloebit and also a lot of strictly OpenSim logic which could be elsewhere.  Ideally we'll better separate this eventually by splitting this class and file up.

This layer is where most of the editing will be necessary when porting to another platform.  As much as possible, the GloebitAPIWrapper interface method signatures should be maintained, but the bodies will likely have to be modified.

## Adding transaction types ---- explain steps


## Integrating ---- explain all interface functions which need to be implemented
