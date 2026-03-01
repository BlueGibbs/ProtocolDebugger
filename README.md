# ProtocolDebugger

This is a basic test harness for understanding the protocol VideoPsalm uses for its "connect to a remote stage view app" feature. There is currently no public documentation on this protocol, so this is to support discovery and reverse engineering efforts.

I'm chasing the VideoPsalm team for more information on this protocol, and will update this README as I learn more about it. In the meantime, if you have any information about this protocol, please feel free to reach out to me.

## Current Status

- The protocol is a simple TCP-based protocol that uses JSON messages to communicate between the client and server.
- The client (VideoPsalm) initiates the connection to the server (the stage view app) and sends a JSON message as a POST to 'update' the stage view with the current state of songs only. When bible verses are selected, remote stage view just sends 'null' for all the properties.

The ProtocolDebugger is a simple TCP server that listens for incoming connections from VideoPsalm and prints the received JSON messages to the console. It also implements a basic in-memory store of songs which are sent back to other clients submitting a GET request to the same endpoint.

To use this tool point VideoPsalm to `http://localhost:2323/remote` as the stage view app, and then run this server. You should see the JSON messages sent by VideoPsalm in the console output. (NB: remote can be changed to anything, but the example embed.html file is using remote as the endpoint.)
The included embed.html file connects to (GET http://localhost:2323/remote) to retrieve the current state of songs and displays the current slide/lyric/verse. You can use this file as a starting point for building your own stage view app that connects to VideoPsalm.

## Future Plans

I will be using this as the basis for some custom lyric display pages for my church. Specifically:
- a lower-thirds display that can be used via a Browser Source in OBS (Without funky chroma-keying)
- a vision-impared display that uses a high-contrast color scheme and large text to make it easier for vision-impaired users to read the lyrics on stage. (This will be implemented via a captive portal WiFi network. The intention is a user scans a QR code and gets the lyrics display on a personal device without extensive setup.)
- Multi-lingual bible verse display on personal devices. But this requires VideoPsalm to send the verse reference in the JSON message, which it currently does not do.