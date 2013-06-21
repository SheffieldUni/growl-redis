growl-redis
===========

Sends Growl for Windows notifications from redis pub/sub. 


Info
----
Needs to run after Growl starts up with the parameters _/server:redisserver /appname:MyApp \[/port:6379\] \[/script:c:\\path\\to\\script.ps1\]_
Runs the powershell script specified on the command line (or subscriptions.ps1) to determine what channels to subscribe to in redis. 
Checks a redis key for each channel at startup, to provide bulletin functionality and then subscribes to each redis channel and
registers the appropriate notification types in Growl.

When a message comes in it decodes it from JSON and triggers a Growl notification.
