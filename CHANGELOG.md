# Changelog

## [0.5.0-alpha.5](https://github.com/APRS-NET/AprsMap/compare/v0.5.0-alpha.4...v0.5.0-alpha.5) (2026-10-25)


### Features

* **desktop:** share the station with browsers, view only ([0b6ae9f](https://github.com/APRS-NET/AprsMap/commit/0b6ae9f4b6e0f3fbf4ecdc2525d436eae980b01b)), closes [#364](https://github.com/APRS-NET/AprsMap/issues/364)
* **updates:** check on start and every two hours, warn when a check fails ([f8e75a7](https://github.com/APRS-NET/AprsMap/commit/f8e75a71d462b591083f7eedd2b91fc0aa20d404))


### Bug Fixes

* **deps:** release APRS.NET 0.6.0-alpha.2 and the OpenTelemetry updates ([341c23d](https://github.com/APRS-NET/AprsMap/commit/341c23d9345ad7fb9eeb8a6efd73f09c3243e9bb))

## [0.5.0-alpha.4](https://github.com/APRS-NET/AprsMap/compare/v0.5.0-alpha.3...v0.5.0-alpha.4) (2026-10-21)


### Features

* **about:** components and versions, from the build ([381e698](https://github.com/APRS-NET/AprsMap/commit/381e6985cfd0e527818bfa1ce39d688811be134a)), closes [#351](https://github.com/APRS-NET/AprsMap/issues/351)
* **devices:** keep the device list up to date from the APRS Foundation ([6dbe074](https://github.com/APRS-NET/AprsMap/commit/6dbe074e0182b8bec1f696ae3d745365cec7f5a7)), closes [#355](https://github.com/APRS-NET/AprsMap/issues/355)
* **gps:** GPS status panel with accuracy, a sky view and the satellites ([2168936](https://github.com/APRS-NET/AprsMap/commit/2168936d4be9fe12e98aa6c82afbe82e488ab5d0)), closes [#331](https://github.com/APRS-NET/AprsMap/issues/331)

## [0.5.0-alpha.3](https://github.com/APRS-NET/AprsMap/compare/v0.5.0-alpha.2...v0.5.0-alpha.3) (2026-10-21)


### Features

* **beacons:** the moving schedule, SmartBeaconing from the GPS ([6098941](https://github.com/APRS-NET/AprsMap/commit/6098941b265a9fd13d59c8565462db32ecfcb80d)), closes [#335](https://github.com/APRS-NET/AprsMap/issues/335)
* **gps:** a GPS source over serial, TCP or gpsd, and the station where it says ([1ecd035](https://github.com/APRS-NET/AprsMap/commit/1ecd03512ba709194d3218544af1869a5e85dda5)), closes [#205](https://github.com/APRS-NET/AprsMap/issues/205)
* **igate:** gate what the TNCs hear to APRS-IS, receive only ([b61ba15](https://github.com/APRS-NET/AprsMap/commit/b61ba15eb9dc537863c9c965d70364bb1522e8ea)), closes [#206](https://github.com/APRS-NET/AprsMap/issues/206)
* **map:** dead reckoning, where moving stations probably are now ([9771a44](https://github.com/APRS-NET/AprsMap/commit/9771a4440c06cb1bf533d264a519ce1c13f5f842)), closes [#198](https://github.com/APRS-NET/AprsMap/issues/198)
* **map:** map contrast from a map button: fade, greyscale and a backing behind symbols ([c07c657](https://github.com/APRS-NET/AprsMap/commit/c07c65757eb0fc19542ade4d56170658c8ba4198)), closes [#345](https://github.com/APRS-NET/AprsMap/issues/345)
* **messaging:** answer APRS queries, back the way they came ([25f08fa](https://github.com/APRS-NET/AprsMap/commit/25f08fab52ab4e392510c60b3b6f2b182833072b)), closes [#200](https://github.com/APRS-NET/AprsMap/issues/200)
* **transmit:** where others see you, from your last position's course and speed ([137d197](https://github.com/APRS-NET/AprsMap/commit/137d19737e43939d666185a91505280e6e6a890f)), closes [#197](https://github.com/APRS-NET/AprsMap/issues/197)

## [0.5.0-alpha.2](https://github.com/APRS-NET/AprsMap/compare/v0.5.0-alpha.1...v0.5.0-alpha.2) (2026-10-21)


### Features

* **about:** Help › About lists where settings, data, tiles and logs are kept, each with Copy and Open folder (F1.31) ([9c603e9](https://github.com/APRS-NET/AprsMap/commit/9c603e914b9578f6858cd04466ffde9b37c58d88)), closes [#306](https://github.com/APRS-NET/AprsMap/issues/306)
* **beacons:** Mic-E position format with a status, and Emergency ([41a1aa8](https://github.com/APRS-NET/AprsMap/commit/41a1aa819ebad930b07595b895775b501f7c3526)), closes [#204](https://github.com/APRS-NET/AprsMap/issues/204)
* **beacons:** saved status texts, grid square or time, and Send status now ([f6a17e4](https://github.com/APRS-NET/AprsMap/commit/f6a17e45644d4a53882e7ba360c70d7537572c89)), closes [#203](https://github.com/APRS-NET/AprsMap/issues/203)
* **filter:** 50 km by default, the map no longer changes the filter, circles on, and the filter said when adding APRS-IS ([9eca25d](https://github.com/APRS-NET/AprsMap/commit/9eca25dc27bd830199060f9307b7f8efeabc75c4))
* **logging:** Settings › Logging with a level per area, a daily log file, and OTLP targets (F1.32) ([f290360](https://github.com/APRS-NET/AprsMap/commit/f290360072529ee95180f768f60397a464434cdf))
* **logging:** Settings › Logging with a level per area, a daily log file, and OTLP targets (F1.32) ([8936a7f](https://github.com/APRS-NET/AprsMap/commit/8936a7f3cc3c7a370876fc19b443574a30efd994)), closes [#310](https://github.com/APRS-NET/AprsMap/issues/310)
* **map:** extra map views that each follow one station ([cfbd5da](https://github.com/APRS-NET/AprsMap/commit/cfbd5da631fce8539109086b32d7f231d258141b)), closes [#207](https://github.com/APRS-NET/AprsMap/issues/207)
* **messages:** answer pings, a remote check that the station is running, hearing and sending (F2.13) ([d9f2080](https://github.com/APRS-NET/AprsMap/commit/d9f2080bd1b23eaaa1510f883e01b90d665ca7af))
* **messages:** answer pings: a remote check that the station is running, hearing and sending (F2.13) ([d77b3d9](https://github.com/APRS-NET/AprsMap/commit/d77b3d9020d63829334473b8fe0368ed10487571)), closes [#307](https://github.com/APRS-NET/AprsMap/issues/307)
* **messaging:** auto-reply while away, once per station, after a delay ([2196145](https://github.com/APRS-NET/AprsMap/commit/219614579815afe203559b5e2f7d25be65d9ce0f)), closes [#202](https://github.com/APRS-NET/AprsMap/issues/202)
* **messaging:** email through the gateway, and asking QRU servers ([862fbff](https://github.com/APRS-NET/AprsMap/commit/862fbffe96090c3fb18e8a4f00ec40f1044b9719)), closes [#201](https://github.com/APRS-NET/AprsMap/issues/201)
* **objects:** place, move, kill and re-send objects and items ([a6cbd71](https://github.com/APRS-NET/AprsMap/commit/a6cbd71c0ff239cd018170448da6f23fb708131c)), closes [#199](https://github.com/APRS-NET/AprsMap/issues/199)


### Bug Fixes

* **beacons:** never send the Mic-E Emergency status ([9ef4e46](https://github.com/APRS-NET/AprsMap/commit/9ef4e4623c61483206a1d6f06712b68c1128d5a4))
* **map:** Clear stations' Clear button clears again ([5fc2dd9](https://github.com/APRS-NET/AprsMap/commit/5fc2dd91a48a89c76d624be69b80d4d9f4f50f1c))
* **messages:** group message history saves, and save what's waiting when the engine stops ([6ee7162](https://github.com/APRS-NET/AprsMap/commit/6ee716214797126f4c546a9e80bf80dd85eba175))
* **messages:** group message history saves, and save what's waiting when the engine stops ([01d3ace](https://github.com/APRS-NET/AprsMap/commit/01d3ace9a440807fa4c65ce44bc33740c80b6db5))
* **messages:** on stopping, write the history only if a save is waiting; keep hosting tests off the real Data folder ([00fc30d](https://github.com/APRS-NET/AprsMap/commit/00fc30df2fe45d5b97b125a3e630a986a9998172))

## [0.5.0-alpha.1](https://github.com/APRS-NET/AprsMap/compare/v0.4.0-alpha.6...v0.5.0-alpha.1) (2026-10-19)


### Miscellaneous Chores

* release 0.5.0-alpha.1 ([3bd0d47](https://github.com/APRS-NET/AprsMap/commit/3bd0d4797c05f773c421d95553ba61ba9286d776))

## [0.4.0-alpha.6](https://github.com/APRS-NET/AprsMap/compare/v0.4.0-alpha.5...v0.4.0-alpha.6) (2026-10-19)


### Features

* **setup:** first run asks the callsign and location, then Finish, Add APRS-IS or Add a TNC; the APRS-IS server is a drop-down (F0.5) ([e3aa68b](https://github.com/APRS-NET/AprsMap/commit/e3aa68bd18256931fbbad476e6d39875dd2e0051)), closes [#295](https://github.com/APRS-NET/AprsMap/issues/295)
* **setup:** no APRS-IS pill for a profile without an APRS-IS source, and each profile keeps its own login (F0.5) ([f7cb335](https://github.com/APRS-NET/AprsMap/commit/f7cb335a1545857ea0e28d9e85aeaf1f98da8389))
* **transmit:** one Transmit switch in the status bar, RX only sources, and refusals that say where to fix them (F2.1) ([27fe8dc](https://github.com/APRS-NET/AprsMap/commit/27fe8dc2be92e6c26af61123a2d00381b53f3787)), closes [#296](https://github.com/APRS-NET/AprsMap/issues/296)


### Bug Fixes

* **engine:** a profile without an APRS-IS source isn't warned about as not set up ([54574dc](https://github.com/APRS-NET/AprsMap/commit/54574dc16212d72458837cd9013c4c18063fcdad))
* **messages:** a new conversation says which way it would go, not that nothing can send ([5f0b1f3](https://github.com/APRS-NET/AprsMap/commit/5f0b1f3f17e614aba970881de9fe5c42e8282f2e))
* **messages:** an RF-only station's messages go out on its TNCs, deleted sources leave Send via, and a waiting message says why (F2.4) ([213b2fb](https://github.com/APRS-NET/AprsMap/commit/213b2fb68d191ae0854fabba61bdab44d78a0d20)), closes [#297](https://github.com/APRS-NET/AprsMap/issues/297)
* **transmit:** an RX-only source's pill always says RX only, not only while transmitting ([f8e1823](https://github.com/APRS-NET/AprsMap/commit/f8e18235ba53ecbb3e9e0b926ec0fbda76d4ebfd))
* **transmit:** the Transmit pill says TX enabled while it's on ([4b8c4fd](https://github.com/APRS-NET/AprsMap/commit/4b8c4fdf3efa2aeb8deca919121f147c24aa46bf))

## [0.4.0-alpha.5](https://github.com/APRS-NET/AprsMap/compare/v0.4.0-alpha.4...v0.4.0-alpha.5) (2026-10-18)


### Features

* **bulletins:** a Bulletins panel of what's heard (F2.5, reading) ([c07911f](https://github.com/APRS-NET/AprsMap/commit/c07911f3f1b0c48648ff68bba3aeac5c343ae067))
* **bulletins:** post bulletins and announcements, and join, leave and post to ANSRVR groups (F2.5) ([5ccfa57](https://github.com/APRS-NET/AprsMap/commit/5ccfa575deef79e9a878bfea353d05428133a4da))
* **capture:** record unexpected packets for the test corpus (F1.27) ([f3c87cc](https://github.com/APRS-NET/AprsMap/commit/f3c87cca618734e30da425486ef02cd9c0d06f76))
* **console:** a source's live traffic, and typing to a serial TNC (F1.28, F3.2) ([f333f06](https://github.com/APRS-NET/AprsMap/commit/f333f06f13e8beb3c3cad3fc8f70afeca512ec2f))
* **dashboard:** My view counts only the stations the view filter shows (F1.22) ([0bb75b9](https://github.com/APRS-NET/AprsMap/commit/0bb75b990b54490a526b48b62620db249b7c9490))
* **dashboard:** sparkline values on hover and by keyboard, and Copy on the tiles (F1.22) ([318d081](https://github.com/APRS-NET/AprsMap/commit/318d0813a6500c98f6e6552f95d1feed70b43b66))
* **gateway:** a public port and an operator port, receive-only logins, a title, and a viewer's page without station controls (F1.26) ([ef62e35](https://github.com/APRS-NET/AprsMap/commit/ef62e353f6d2167faf77db414da718cb525f99e3))
* **gateway:** home privacy for viewers: the square's centre, or no location (F1.26) ([1641bb9](https://github.com/APRS-NET/AprsMap/commit/1641bb95e350ff45ce985226b54bcc0f0938640b))
* **gateway:** operator sign-in: operators on the host, a session cookie, guessing limits, and roles on every hub call (F2.9) ([d08d2e3](https://github.com/APRS-NET/AprsMap/commit/d08d2e36c1a10db7add194ed2a68c257e44b8cb5))
* **installers:** installers that update in the app, and each system's own folders for settings, data and cache (F4.15) ([d571a62](https://github.com/APRS-NET/AprsMap/commit/d571a62e2b4cb4120489dbdde2b409ef7e18b80b))
* **layout:** save layouts by name and ask to use one when the monitors change (F1.23) ([d7414fa](https://github.com/APRS-NET/AprsMap/commit/d7414fa336c83047011030ed880f9a2fa9dc351a))
* **map:** a destination with its distance, bearing and line (F1.18) ([88f2381](https://github.com/APRS-NET/AprsMap/commit/88f2381747c6ee85f5981a910a9c53fa876ce069))
* **map:** my last position's routes out, through each digipeater to each IGate (F3.3) ([e26bdfd](https://github.com/APRS-NET/AprsMap/commit/e26bdfda5070ae70cc213fda75d7ca464e444d89))
* **shell:** any panel can be a tab beside the map (F1.0 behavior 16) ([3416274](https://github.com/APRS-NET/AprsMap/commit/34162745fa0b360dc93c7b98a53fa288256eba21))
* **sources:** KISS TNCs on a serial port (F3.1) ([3089bcf](https://github.com/APRS-NET/AprsMap/commit/3089bcffd89f56522898ae07aede04f392e6166c))
* **sources:** radios' built-in TNCs, by TNC type or in APRS mode (F3.2) ([28447c1](https://github.com/APRS-NET/AprsMap/commit/28447c1679a4f2c3c98c43255194722106ac713a))
* **transmit:** how my last position got out (F3.3) ([6cf775f](https://github.com/APRS-NET/AprsMap/commit/6cf775f530fd79d80c732aed2cb517a6c8ce65d7))
* **ui:** actions leave Settings for the menus; Reset layout asks and can be undone (F1.29) ([8fb443e](https://github.com/APRS-NET/AprsMap/commit/8fb443eb5ca602402152068b8d34bbf9521f81fa))
* **ui:** File, View, Station, Tools and Help menus, hidden until Alt (F1.29) ([43757ac](https://github.com/APRS-NET/AprsMap/commit/43757ace8f00bea48786f9f9c9dd039652bcdf10))
* **updates:** a daily check of the releases, a quiet notice, and the gateway operator's image to pull (F4.15) ([c128332](https://github.com/APRS-NET/AprsMap/commit/c128332078130b726f0410c566e718d49305fe4e))
* **web:** sign in as the gateway's operator from the app menu (F2.9) ([adc32d8](https://github.com/APRS-NET/AprsMap/commit/adc32d869557cfc33a14ded59c04e19c2b0a1468))
* **wizard:** a TNC on a serial port in Use a TNC instead (F3.1, F3.2) ([a18919f](https://github.com/APRS-NET/AprsMap/commit/a18919fb9d12ad92b6eb6150872d30dd820b1e13))


### Bug Fixes

* **messages:** an open conversation takes the whole panel, with Back to the list ([627f48b](https://github.com/APRS-NET/AprsMap/commit/627f48b677836d7cae05a5c5783193caaa47a0f8))
* **tiles:** a tile read while a refresh replaces it no longer fails on Windows ([e3e9519](https://github.com/APRS-NET/AprsMap/commit/e3e9519d82f3a821d6537d2b43a101517f411893))
* **ui:** TNC commands is a submenu, without an ellipsis ([677960d](https://github.com/APRS-NET/AprsMap/commit/677960d4114a55c8281b16fb35d8360b9cf58a68))

## [0.4.0-alpha.4](https://github.com/APRS-NET/AprsMap/compare/v0.4.0-alpha.3...v0.4.0-alpha.4) (2026-10-17)


### Features

* **beacons:** position and status beacons (F2.2) ([8e74f56](https://github.com/APRS-NET/AprsMap/commit/8e74f563889d07ff60a5891d6779d420813cf5e3))

## [0.4.0-alpha.3](https://github.com/APRS-NET/AprsMap/compare/v0.4.0-alpha.2...v0.4.0-alpha.3) (2026-10-17)


### Features

* **engine:** messaging on APRS.NET's MessagingStation, with Send via, the offline queue and kept conversations ([940191e](https://github.com/APRS-NET/AprsMap/commit/940191ee24705fc7e0005d5733858a064986fb7f))
* **engine:** own station and the Transmit switch, with Send position now ([1e2725c](https://github.com/APRS-NET/AprsMap/commit/1e2725ccd8b05aea765b02093f21d68ed7a84eeb))
* **engine:** transmit per source, with Disable TX, the licence on the profile, Test send and listener IDs ([e0c449c](https://github.com/APRS-NET/AprsMap/commit/e0c449c827625258a14e5881e4c716d140c16772))
* **ui:** one licensed checkbox on the profile: unticked listens with a listener ID, ticked shows the callsign and Disable TX ([be698c6](https://github.com/APRS-NET/AprsMap/commit/be698c6797d4492e1796a97884fe758569a3e8e3))
* **ui:** the Messages panel, with conversations, Send via, retries and unread counts ([0b99790](https://github.com/APRS-NET/AprsMap/commit/0b99790eabf4f89b58f9c9712c5752f4ffdfbdf2))
* **ui:** Transmit and Send position now in the connection menu, the station's symbol and comment ([4bd8034](https://github.com/APRS-NET/AprsMap/commit/4bd803483abfe633d017969784a5e8affeb64570))
* **ui:** TX enabled, TX path and Test send per source, listen only and the licence on the profile, Send position now per pill ([c93bfe2](https://github.com/APRS-NET/AprsMap/commit/c93bfe22275b4a0bbf8de16b1ea1a0393fce97a1))


### Bug Fixes

* **ui:** the packet list pauses only when the viewer scrolls or clicks a row ([30e869a](https://github.com/APRS-NET/AprsMap/commit/30e869a20f20fb2692dffcb9d45bbcb7c9fda353))

## [0.4.0-alpha.2](https://github.com/APRS-NET/AprsMap/compare/v0.4.0-alpha.1...v0.4.0-alpha.2) (2026-10-17)


### Bug Fixes

* **ui:** a floating panel or dashboard shows its view, and the dashboard docks beside the map again from its tab menu ([3f30f4e](https://github.com/APRS-NET/AprsMap/commit/3f30f4eccdd88c9503fa863be98f6fc00f27b7f2))

## [0.4.0-alpha.1](https://github.com/APRS-NET/AprsMap/compare/v0.3.0-alpha.4...v0.4.0-alpha.1) (2026-10-17)


### Documentation

* a milestone's Release-As footer goes on a new commit ([53cdc99](https://github.com/APRS-NET/AprsMap/commit/53cdc994208f7deba92062923218630ace8339c9))

## [0.3.0-alpha.4](https://github.com/APRS-NET/AprsMap/compare/v0.3.0-alpha.3...v0.3.0-alpha.4) (2026-10-16)


### Features

* filter the packet list by source (F3.1) ([acf6782](https://github.com/APRS-NET/AprsMap/commit/acf67829dd63ca397d0ddd47ec4a3fac2247e11e))
* select this station on the map, with what its radio heard (F3.1) ([78e00b1](https://github.com/APRS-NET/AprsMap/commit/78e00b1e506001fa74612a3d32463d3f5b27771a))
* **ui:** each TNC's switch on the dashboard's Connection rows (F3.1) ([e0f0ccb](https://github.com/APRS-NET/AprsMap/commit/e0f0ccbb606f5a412cb2dce7da7fce9272ca0dcf))
* **ui:** Use a TNC instead in the setup wizard (F3.1) ([dc04842](https://github.com/APRS-NET/AprsMap/commit/dc04842ca1dc10fa69e8f5f16ab439fd3d021225))

## [0.3.0-alpha.3](https://github.com/APRS-NET/AprsMap/compare/v0.3.0-alpha.2...v0.3.0-alpha.3) (2026-10-16)


### Features

* **engine:** dashboard counters for the last 24 hours (F1.22) ([9bd8933](https://github.com/APRS-NET/AprsMap/commit/9bd893311b3febe98c068a1f6d2cec85be043295))
* flag position reports that don't fit their station's track (F1.20) ([ea6c6c6](https://github.com/APRS-NET/AprsMap/commit/ea6c6c6f9ae8bdddca2ac60d0416cace0e01acfd))
* KISS over TCP sources, receive only (F3.1) ([4a956aa](https://github.com/APRS-NET/AprsMap/commit/4a956aa21fbea62292657832cf0b01d515773170))
* **map:** RF links for the selected station only, dashed, with Show all RF connections ([e80a6fd](https://github.com/APRS-NET/AprsMap/commit/e80a6fdfb805d86fa64bd7c938598f773924b46b))
* **map:** toggle all RF connections from the map and the app menu ([11f6c90](https://github.com/APRS-NET/AprsMap/commit/11f6c901d152fbc879a17690a46f804b9e28d868))
* packets a TNC heard are heard by this station (F3.1) ([a0f9964](https://github.com/APRS-NET/AprsMap/commit/a0f9964745ea7ad63d83f5a9291ca3f9ee31cfb6))
* **packets:** save as text, CSV, JSON, indented JSON or YAML ([4eb4ef9](https://github.com/APRS-NET/AprsMap/commit/4eb4ef9bc5fc336a3fdfb757036e9b0266c84373))
* telemetry with the station's names, units and scaling (F1.11) ([676d400](https://github.com/APRS-NET/AprsMap/commit/676d400ed38bcdc1220b95f6fd202cc9987e00ac))
* **ui:** a Clear stations button on the map (F1.19) ([ff9f6a7](https://github.com/APRS-NET/AprsMap/commit/ff9f6a799260d0da1dab09e3c92256426333b474))
* **ui:** a Dashboard tab beside the map, with live tiles and sparklines (F1.22) ([3de0e67](https://github.com/APRS-NET/AprsMap/commit/3de0e67c53f387ae8eea7678cc005f1abeb4c416))
* **ui:** an APRS-IS on/off switch beside the connection pill and in the dashboard ([1479209](https://github.com/APRS-NET/AprsMap/commit/1479209454ce4560f135a093f59be0d28ffd8cb6))
* **ui:** each TNC's pill works like the APRS-IS one, with a switch and Reconnect now (F3.1) ([d9deeaf](https://github.com/APRS-NET/AprsMap/commit/d9deeaf99d10134844ea76e6113b52ea63b227da))
* **ui:** the dashboard's farthest direct hops, weather extremes and statistics tables (F1.22) ([56473f9](https://github.com/APRS-NET/AprsMap/commit/56473f911d69d5ce4d8133f67ebefbe33827017d))


### Bug Fixes

* **map:** the RF connections toggle lives on the map only, not in the app menu ([cc5e31d](https://github.com/APRS-NET/AprsMap/commit/cc5e31d1c0fc1e02d22c16c17939f01be843c392))
* **packets:** keep the Save menu filled rather than refilling it as it opens ([f04ba40](https://github.com/APRS-NET/AprsMap/commit/f04ba40817993483f71d010123c475f3faaac905))
* **packets:** resuming the packet list scrolls back to the newest packets ([71e6aa5](https://github.com/APRS-NET/AprsMap/commit/71e6aa523852c6077f1568305d7345913ba7da2d))
* **ui:** a table row no filter fits is drawn as a plain row, not a disabled button ([fe249b5](https://github.com/APRS-NET/AprsMap/commit/fe249b5b40b2f0842a2762c598c1fd5dd319d009))
* **ui:** the APRS-IS switch sits inside the pill, which says APRS-IS off itself ([b75f5fa](https://github.com/APRS-NET/AprsMap/commit/b75f5fa8acf746569d17f0a69a27845c1ed0743a))

## [0.3.0-alpha.2](https://github.com/APRS-NET/AprsMap/compare/v0.3.0-alpha.1...v0.3.0-alpha.2) (2026-10-16)


### Features

* a View filter for the map and the station list (F1.9) ([6186b9a](https://github.com/APRS-NET/AprsMap/commit/6186b9ad42487880ce5b5b4bcdef3c55257e2c45)), closes [#123](https://github.com/APRS-NET/AprsMap/issues/123)
* area objects drawn as their shapes, and signposts (F1.12) ([09e487e](https://github.com/APRS-NET/AprsMap/commit/09e487ef9a7e9c717dab8604a87cee0ef57b6ac9)), closes [#126](https://github.com/APRS-NET/AprsMap/issues/126)
* clear stations, stale stations, tracks or the packet history (F1.19) ([efca48f](https://github.com/APRS-NET/AprsMap/commit/efca48f9409c5714e80c3ecccd1b868cfd96194d)), closes [#128](https://github.com/APRS-NET/AprsMap/issues/128)
* flag stations that send invalid packets (F1.20) ([085a079](https://github.com/APRS-NET/AprsMap/commit/085a07980810dc766eb5595c48451c59456af480))
* pick the station's location on a map in the setup wizard (F0.5) ([76a36ea](https://github.com/APRS-NET/AprsMap/commit/76a36ea983b726f065da36e9c406ae2113b92ec6)), closes [#130](https://github.com/APRS-NET/AprsMap/issues/130)
* quick toggles for the Internet, APRS-IS and map tiles (F1.15) ([0da0536](https://github.com/APRS-NET/AprsMap/commit/0da0536330915acb2ad496de8f2d0890b49dc8ec)), closes [#129](https://github.com/APRS-NET/AprsMap/issues/129)
* show the selected packet's path, dotted to the IGate when heard directly (F1.21) ([809f2e8](https://github.com/APRS-NET/AprsMap/commit/809f2e83cb1323a2f355e13ad9687464112ccc62))
* telemetry in the details, raw for now (F1.11) ([5a53cd1](https://github.com/APRS-NET/AprsMap/commit/5a53cd1d95f0271cd2550b1889e14a258457b104))
* the RF network over the last hour on the map (F1.21) ([09fe30e](https://github.com/APRS-NET/AprsMap/commit/09fe30ee748a9b60865f4c402b4d558c5688671d)), closes [#122](https://github.com/APRS-NET/AprsMap/issues/122)
* tracks behind moving stations, with Clear track (F1.5) ([3361184](https://github.com/APRS-NET/AprsMap/commit/3361184585b92dea81528dff4a6056872f402134)), closes [#121](https://github.com/APRS-NET/AprsMap/issues/121)
* weather readings in the details and on the map, and Settings › Units (F1.10) ([0a88fda](https://github.com/APRS-NET/AprsMap/commit/0a88fdafddd31d7d79adf39320dc3352a68a848d)), closes [#124](https://github.com/APRS-NET/AprsMap/issues/124)


### Bug Fixes

* **ui:** the wizard's filter help says the text is added to the automatic filter ([f602cf7](https://github.com/APRS-NET/AprsMap/commit/f602cf7ebb805d6444171c75750c490b80e9d9fc))

## [0.3.0-alpha.1](https://github.com/APRS-NET/AprsMap/compare/v0.2.0-alpha.4...v0.3.0-alpha.1) (2026-10-16)


### Miscellaneous Chores

* **deps:** APRS.NET 0.2.0-alpha.3, and release 0.3.0-alpha.1 ([313f1ed](https://github.com/APRS-NET/AprsMap/commit/313f1ed826154f8d26054e754d0114bb85c8f52a))

## [0.2.0-alpha.4](https://github.com/APRS-NET/AprsMap/compare/v0.2.0-alpha.3...v0.2.0-alpha.4) (2026-10-16)


### Features

* **ui:** the APRS-IS pill is light green when connected, with a receive-only mark ([0bc0c0f](https://github.com/APRS-NET/AprsMap/commit/0bc0c0f2b816534db03e97e776706ad5117364cd))


### Bug Fixes

* **map:** never read styled properties from the render thread ([3d6b518](https://github.com/APRS-NET/AprsMap/commit/3d6b51886a46119641b712f3d0808d3e171d8a78))
* **ui:** Show the filter's ranges on the map is remembered ([6439853](https://github.com/APRS-NET/AprsMap/commit/6439853ea50ff70abc632137bc84014e6b160808))
* **ui:** the app menu opens filled on desktop, and a touchpad zooms the map ([7f442b9](https://github.com/APRS-NET/AprsMap/commit/7f442b9a7ea00b4d20ec583bcaf627fa5b545bea))

## [0.2.0-alpha.3](https://github.com/APRS-NET/AprsMap/compare/v0.2.0-alpha.2...v0.2.0-alpha.3) (2026-10-16)


### Features

* **engine:** build the APRS-IS filter from my range, map views and followed stations ([b802d2c](https://github.com/APRS-NET/AprsMap/commit/b802d2cf82e221a39f0ff331933fd8074086255d))
* **engine:** distance and bearing from my location ([5ad0c58](https://github.com/APRS-NET/AprsMap/commit/5ad0c586c92619fe3a9b5cce1858b03f2fe433f8))
* **engine:** query the station list a page at a time ([f09887d](https://github.com/APRS-NET/AprsMap/commit/f09887d06c095c87eda61b2c23014431bb27b731))
* **engine:** several sources at once, with the source of every packet and where else it was seen ([b1079c2](https://github.com/APRS-NET/AprsMap/commit/b1079c2502f3acd412dbc6b0b4b9e76118f1db19))
* **engine:** sources in the settings, with profiles picking from them ([c8e2ab9](https://github.com/APRS-NET/AprsMap/commit/c8e2ab9c040cf5ad3e855df17f30c65dc62ec306))
* **engine:** station details say what sent the last packet and how it arrived ([8612cad](https://github.com/APRS-NET/AprsMap/commit/8612cad8e423d233fc45e8db5f7355390826ead8))
* **gateway:** run behind a proxy, with security headers and caching ([90cc78c](https://github.com/APRS-NET/AprsMap/commit/90cc78ca0e382e83cc1654727834b9a667f519a5))
* **gateway:** say when the gateway is reconnecting, and when the page is out of date ([c3673cf](https://github.com/APRS-NET/AprsMap/commit/c3673cf93a2323c6e594a1fd4400890e34a26979))
* **gateway:** viewer limits, rate limiting and the viewer count ([dc3b10b](https://github.com/APRS-NET/AprsMap/commit/dc3b10b298e9d200ba4622d17f8250115c55c684))
* **map:** my location as a house, and Set my location here with Undo ([b3a74a7](https://github.com/APRS-NET/AprsMap/commit/b3a74a739bc604b8317c34c89cec8737ad7f8e58))
* **packets:** direct, digipeated and duplicate markers; duplicates don't update stations ([#95](https://github.com/APRS-NET/AprsMap/issues/95)) ([a2ba9c2](https://github.com/APRS-NET/AprsMap/commit/a2ba9c29645b910492b844e1e3944dc90dcae499))
* **ui:** report the map for the filter, set the range, and show the filter ([94dd44b](https://github.com/APRS-NET/AprsMap/commit/94dd44be767f9502dc31b0a823dd709f9c3cf43f))
* **ui:** Settings &gt; Sources, and profiles picking their source ([6097399](https://github.com/APRS-NET/AprsMap/commit/609739929153b82dadb4205b9b56078f6127aa75))
* **ui:** the Details panel ([e4e1317](https://github.com/APRS-NET/AprsMap/commit/e4e131747092f668dc5c89fc6fb1b933e32a68db))
* **ui:** the Stations panel ([891d0fb](https://github.com/APRS-NET/AprsMap/commit/891d0fb149479830ef64418918747396f9997463))


### Bug Fixes

* **engine:** a generic destination names no device ([2562e06](https://github.com/APRS-NET/AprsMap/commit/2562e06abf314ddb248acaeb7c79b7df07ff70f6))
* **ui:** source choices fit at double text size ([c8a1ac6](https://github.com/APRS-NET/AprsMap/commit/c8a1ac67f552d450d7a617a3d9c54f35ddb89694))

## [0.2.0-alpha.2](https://github.com/APRS-NET/AprsMap/compare/v0.2.0-alpha.1...v0.2.0-alpha.2) (2026-10-15)


### Features

* **core:** a station store with objects, aging and a cap ([fa460d0](https://github.com/APRS-NET/AprsMap/commit/fa460d077d440778f7d3ca707ea69d52759cb35c))
* **engine:** packet header, kind, parse issues and a filtered packet stream ([05e0956](https://github.com/APRS-NET/AprsMap/commit/05e0956413ad46c56b7cc9dc368826ad54e1f462))
* **tiles:** a policy-following tile cache, in process and behind the gateway ([de20f29](https://github.com/APRS-NET/AprsMap/commit/de20f29778174b7ee1cbf769b60a794599da72c3))
* **ui:** dockable panels around the map, with layouts that persist and stay on screen ([9a6e480](https://github.com/APRS-NET/AprsMap/commit/9a6e480e785992d8c8e6a1a143ec2fa0667b3bca))
* **ui:** move, show and gather panels from menus; Ctrl+Tab; narrow-window tabs ([4b709c7](https://github.com/APRS-NET/AprsMap/commit/4b709c78b5c7b030e09048184e3fb9816af9857f))
* **ui:** Settings › Map ([22787fc](https://github.com/APRS-NET/AprsMap/commit/22787fcd990cec4a5245c4dd21c4ad962f66eae8))
* **ui:** start over from Settings › Data, or with --reset-all ([db74d8b](https://github.com/APRS-NET/AprsMap/commit/db74d8b4ee859737e61547259319862ec354008e)), closes [#81](https://github.com/APRS-NET/AprsMap/issues/81)
* **ui:** stations on the map, with symbols, labels and selection ([e1296c6](https://github.com/APRS-NET/AprsMap/commit/e1296c6b74a752bdea797e1bd29061af0343baf7))
* **ui:** the map, with tiles through the app's cache ([0085aca](https://github.com/APRS-NET/AprsMap/commit/0085aca88c73d81fbef4d404de79ab3af464b965))
* **ui:** the Packets panel with filters, pause, parse issues and save ([5eabc68](https://github.com/APRS-NET/AprsMap/commit/5eabc68ec89760252de442c29d926eea7a6bdf64))


### Bug Fixes

* **map:** tolerate a tile landing while the tile layer is disposed ([87ce2c8](https://github.com/APRS-NET/AprsMap/commit/87ce2c8490e60244551db7ce937b3d910c322ba9))
* **ui:** use English plural rules when no locale is set ([8cddedd](https://github.com/APRS-NET/AprsMap/commit/8cddedd3322bf723a73c21849b7144ae800da4fb))


### Performance Improvements

* **map:** hold 60 fps with 2,000 stations in the browser ([5af696e](https://github.com/APRS-NET/AprsMap/commit/5af696e492057b7ec6ca58cae27b409a165c45df))

## [0.2.0-alpha.1](https://github.com/APRS-NET/AprsMap/compare/v0.1.0-alpha.10...v0.2.0-alpha.1) (2026-10-15)


### Features

* **core:** run the APRS-IS session on APRS.NET's AprsIsConnection ([38e92f4](https://github.com/APRS-NET/AprsMap/commit/38e92f4e99b78f5007bab7a57f7864b6d2f758b3))
* **ui:** accessibility checks, touch targets and 200 % layout fixes ([f3c55bc](https://github.com/APRS-NET/AprsMap/commit/f3c55bc0feeb5baad7aca02ffff248253e1de759))
* **ui:** appearance palettes and per-viewer preferences ([1818e3c](https://github.com/APRS-NET/AprsMap/commit/1818e3c547ed2a51b21c64126f9d0c26b6deeadf))
* **ui:** manage station profiles in Settings ([de2a838](https://github.com/APRS-NET/AprsMap/commit/de2a838a3c815b89ce1a3d18f7c9ac1650bcdc59))
* **ui:** Settings screen with Appearance and About ([e86ba03](https://github.com/APRS-NET/AprsMap/commit/e86ba03a4f5f642f6ce058de25fbe9541d6395cc))


### Bug Fixes

* **desktop:** pass the pseudo-language setting's value, not the configuration ([aa3b209](https://github.com/APRS-NET/AprsMap/commit/aa3b209e38c65d67bef563c39d1f02a2a5feaf8a))
* **hosting:** export APRS.NET's metrics and traces ([052e44a](https://github.com/APRS-NET/AprsMap/commit/052e44a38f68af373f424df10cff33c3292e8dec))


### Documentation

* list APRS.NET 0.2.0's metrics in the README ([b355c2d](https://github.com/APRS-NET/AprsMap/commit/b355c2dad64510e6880cf3a42b1dd13ba99c8746))

## [0.1.0-alpha.10](https://github.com/APRS-NET/AprsMap/compare/v0.1.0-alpha.9...v0.1.0-alpha.10) (2026-10-15)


### Features

* use the APRS.NET icon for the desktop and web apps ([f4f0a88](https://github.com/APRS-NET/AprsMap/commit/f4f0a88f2f4fa6ae01b4c1c967c6c5a23bc09256))

## [0.1.0-alpha.9](https://github.com/APRS-NET/AprsMap/compare/v0.1.0-alpha.8...v0.1.0-alpha.9) (2026-10-15)


### Features

* **ui:** this device's location in the setup wizard ([c39c119](https://github.com/APRS-NET/AprsMap/commit/c39c119dec67729038c6c2d6dcfdc8988a4b6bfa))


### Bug Fixes

* **browser:** explain a failed start instead of staying on "Loading…" ([ab5dbb4](https://github.com/APRS-NET/AprsMap/commit/ab5dbb48c0f32e05085fd96e63a16b48dfc2b447))

## [0.1.0-alpha.8](https://github.com/APRS-NET/AprsMap/compare/v0.1.0-alpha.7...v0.1.0-alpha.8) (2026-10-15)


### Features

* **ui:** first-run setup wizard ([4e1bea3](https://github.com/APRS-NET/AprsMap/commit/4e1bea319a165d8a304f1604f5c730569fb758b5))


### Bug Fixes

* **engine:** log in with a range filter when a profile has none ([61f8fd0](https://github.com/APRS-NET/AprsMap/commit/61f8fd0dda8db6285170606b8b4548fb4e5fd018))

## [0.1.0-alpha.7](https://github.com/APRS-NET/AprsMap/compare/v0.1.0-alpha.6...v0.1.0-alpha.7) (2026-10-15)


### Features

* **engine:** server presets and Maidenhead locators for the setup wizard ([14205e5](https://github.com/APRS-NET/AprsMap/commit/14205e5c89219e15f94ab7e84b0be07d6b132cfc))

## [0.1.0-alpha.6](https://github.com/APRS-NET/AprsMap/compare/v0.1.0-alpha.5...v0.1.0-alpha.6) (2026-10-15)


### Features

* **engine:** report errors as codes the UI can translate ([96f62d9](https://github.com/APRS-NET/AprsMap/commit/96f62d90399ba897c27f5a54e8049ed2e4e935eb))
* **ui:** move UI text to resources with a live language switch ([6cdcd56](https://github.com/APRS-NET/AprsMap/commit/6cdcd563252abc62e0b9931e90a0c9de59745a02))

## [0.1.0-alpha.5](https://github.com/APRS-NET/AprsMap/compare/v0.1.0-alpha.4...v0.1.0-alpha.5) (2026-10-14)


### Features

* **engine:** address lookup and passcode calculator for the setup wizard ([b95a066](https://github.com/APRS-NET/AprsMap/commit/b95a066d40425d9b1cc665c3679b075ea63b62e8))

## [0.1.0-alpha.4](https://github.com/APRS-NET/AprsMap/compare/v0.1.0-alpha.3...v0.1.0-alpha.4) (2026-10-14)


### Features

* **settings:** per-user settings with station profiles and switching ([f11f587](https://github.com/APRS-NET/AprsMap/commit/f11f587ec7959b208b5ecda726db38684606c0f8))

## [0.1.0-alpha.3](https://github.com/APRS-NET/AprsMap/compare/v0.1.0-alpha.2...v0.1.0-alpha.3) (2026-10-14)


### Features

* **engine:** stream the map's stations and survive gateway restarts ([823aabd](https://github.com/APRS-NET/AprsMap/commit/823aabd8b5451af49f107e79c3bdd9a892edc034))

## [0.1.0-alpha.2](https://github.com/APRS-NET/AprsMap/compare/v0.1.0-alpha.1...v0.1.0-alpha.2) (2026-10-14)


### Bug Fixes

* **web:** give the SignalR hub source-generated JSON metadata ([d595d29](https://github.com/APRS-NET/AprsMap/commit/d595d2933cceec240085285afaf0d3c6b6d9d96a))

## 0.1.0-alpha.1 (2026-10-13)


### Features

* scaffold the .NET 10 solution, CI, releases and container ([13aaeb8](https://github.com/APRS-NET/AprsMap/commit/13aaeb86c55804291c6bf62656b04e53a982d705))


### Bug Fixes

* **ci:** package report header and parallel trace test ([87146fd](https://github.com/APRS-NET/AprsMap/commit/87146fdb138678ad0fd0f4b898dff77e76d2650e))


### Documentation

* note where the alpha series starts ([9570ed6](https://github.com/APRS-NET/AprsMap/commit/9570ed6a061e99d558a60876b5e65d7725d25cbc))
