# Changelog

## [0.3.0-alpha.4](https://github.com/APRS-NET/AprsMap/compare/v0.3.0-alpha.3...v0.3.0-alpha.4) (2026-10-16)


### Features

* filter the packet list by source (F3.1) ([acf6782](https://github.com/APRS-NET/AprsMap/commit/acf67829dd63ca397d0ddd47ec4a3fac2247e11e))
* select this station on the map, with what its radio heard (F3.1) ([a4005e0](https://github.com/APRS-NET/AprsMap/commit/a4005e0e3d5b0b50e7ae61feb9a71092f2ef7593))
* select this station on the map, with what its radio heard (F3.1) ([78e00b1](https://github.com/APRS-NET/AprsMap/commit/78e00b1e506001fa74612a3d32463d3f5b27771a))
* **ui:** each TNC's switch on the dashboard's Connection rows (F3.1) ([e0f0ccb](https://github.com/APRS-NET/AprsMap/commit/e0f0ccbb606f5a412cb2dce7da7fce9272ca0dcf))
* **ui:** Use a TNC instead in the setup wizard (F3.1) ([dc04842](https://github.com/APRS-NET/AprsMap/commit/dc04842ca1dc10fa69e8f5f16ab439fd3d021225))

## [0.3.0-alpha.3](https://github.com/APRS-NET/AprsMap/compare/v0.3.0-alpha.2...v0.3.0-alpha.3) (2026-10-16)


### Features

* **engine:** dashboard counters for the last 24 hours (F1.22) ([9bd8933](https://github.com/APRS-NET/AprsMap/commit/9bd893311b3febe98c068a1f6d2cec85be043295))
* flag position reports that don't fit their station's track (F1.20) ([ea6c6c6](https://github.com/APRS-NET/AprsMap/commit/ea6c6c6f9ae8bdddca2ac60d0416cace0e01acfd))
* KISS over TCP sources, receive only (F3.1) ([4a956aa](https://github.com/APRS-NET/AprsMap/commit/4a956aa21fbea62292657832cf0b01d515773170))
* **map:** RF links for the selected station only, dashed, with Show all RF connections ([e80a6fd](https://github.com/APRS-NET/AprsMap/commit/e80a6fdfb805d86fa64bd7c938598f773924b46b))
* **map:** toggle all RF connections from a button on the map ([b62d4d0](https://github.com/APRS-NET/AprsMap/commit/b62d4d0fb10a65b35f976832877d31229e2ea982))
* **map:** toggle all RF connections from the map and the app menu ([11f6c90](https://github.com/APRS-NET/AprsMap/commit/11f6c901d152fbc879a17690a46f804b9e28d868))
* packets a TNC heard are heard by this station (F3.1) ([c7f1117](https://github.com/APRS-NET/AprsMap/commit/c7f1117bbd8876c5cfce35fbc37d64f993c42397))
* packets a TNC heard are heard by this station (F3.1) ([a0f9964](https://github.com/APRS-NET/AprsMap/commit/a0f9964745ea7ad63d83f5a9291ca3f9ee31cfb6))
* **packets:** save as text, CSV, JSON, indented JSON or YAML ([912a75c](https://github.com/APRS-NET/AprsMap/commit/912a75cc91f4a884bce3199e0208fab25e4bad12))
* **packets:** save as text, CSV, JSON, indented JSON or YAML ([4eb4ef9](https://github.com/APRS-NET/AprsMap/commit/4eb4ef9bc5fc336a3fdfb757036e9b0266c84373))
* telemetry with the station's names, units and scaling (F1.11) ([676d400](https://github.com/APRS-NET/AprsMap/commit/676d400ed38bcdc1220b95f6fd202cc9987e00ac))
* **ui:** a Clear stations button on the map (F1.19) ([b2afd86](https://github.com/APRS-NET/AprsMap/commit/b2afd86b23980e90f7ceb0a5e234722bd9378835))
* **ui:** a Clear stations button on the map (F1.19) ([ff9f6a7](https://github.com/APRS-NET/AprsMap/commit/ff9f6a799260d0da1dab09e3c92256426333b474))
* **ui:** a Dashboard tab beside the map, with live tiles and sparklines (F1.22) ([3de0e67](https://github.com/APRS-NET/AprsMap/commit/3de0e67c53f387ae8eea7678cc005f1abeb4c416))
* **ui:** an APRS-IS on/off switch beside the connection pill and in the dashboard ([1479209](https://github.com/APRS-NET/AprsMap/commit/1479209454ce4560f135a093f59be0d28ffd8cb6))
* **ui:** each TNC's pill works like the APRS-IS one, with a switch and Reconnect now (F3.1) ([d9deeaf](https://github.com/APRS-NET/AprsMap/commit/d9deeaf99d10134844ea76e6113b52ea63b227da))
* **ui:** the dashboard's farthest direct hops, weather extremes and statistics tables (F1.22) ([1860fc8](https://github.com/APRS-NET/AprsMap/commit/1860fc83b5d38363153217d5323fb42c0b4c6f22))
* **ui:** the dashboard's farthest direct hops, weather extremes and statistics tables (F1.22) ([56473f9](https://github.com/APRS-NET/AprsMap/commit/56473f911d69d5ce4d8133f67ebefbe33827017d))


### Bug Fixes

* **map:** the RF connections toggle lives on the map only, not in the app menu ([cc5e31d](https://github.com/APRS-NET/AprsMap/commit/cc5e31d1c0fc1e02d22c16c17939f01be843c392))
* **packets:** keep the Save menu filled rather than refilling it as it opens ([f04ba40](https://github.com/APRS-NET/AprsMap/commit/f04ba40817993483f71d010123c475f3faaac905))
* **packets:** resuming the packet list scrolls back to the newest packets ([1050512](https://github.com/APRS-NET/AprsMap/commit/1050512193c3ecfb949192ce1714f423008baabb))
* **packets:** resuming the packet list scrolls back to the newest packets ([71e6aa5](https://github.com/APRS-NET/AprsMap/commit/71e6aa523852c6077f1568305d7345913ba7da2d))
* **ui:** a table row no filter fits is drawn as a plain row, not a disabled button ([fe249b5](https://github.com/APRS-NET/AprsMap/commit/fe249b5b40b2f0842a2762c598c1fd5dd319d009))
* **ui:** the APRS-IS switch sits inside the pill, which says APRS-IS off itself ([b75f5fa](https://github.com/APRS-NET/AprsMap/commit/b75f5fa8acf746569d17f0a69a27845c1ed0743a))

## [0.3.0-alpha.2](https://github.com/APRS-NET/AprsMap/compare/v0.3.0-alpha.1...v0.3.0-alpha.2) (2026-10-16)


### Features

* a View filter for the map and the station list (F1.9) ([6186b9a](https://github.com/APRS-NET/AprsMap/commit/6186b9ad42487880ce5b5b4bcdef3c55257e2c45)), closes [#123](https://github.com/APRS-NET/AprsMap/issues/123)
* area objects drawn as their shapes, and signposts (F1.12) ([09e487e](https://github.com/APRS-NET/AprsMap/commit/09e487ef9a7e9c717dab8604a87cee0ef57b6ac9)), closes [#126](https://github.com/APRS-NET/AprsMap/issues/126)
* clear stations, stale stations, tracks or the packet history (F1.19) ([efca48f](https://github.com/APRS-NET/AprsMap/commit/efca48f9409c5714e80c3ecccd1b868cfd96194d)), closes [#128](https://github.com/APRS-NET/AprsMap/issues/128)
* flag stations that send invalid packets (F1.20) ([2d7c1fe](https://github.com/APRS-NET/AprsMap/commit/2d7c1fe6f6150011f0d5b526b64670cd6433f3f4))
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

* **map:** never read styled properties from the render thread ([588d08c](https://github.com/APRS-NET/AprsMap/commit/588d08cef19ac3028c41dd5006b09987b947d158))
* **map:** never read styled properties from the render thread ([3d6b518](https://github.com/APRS-NET/AprsMap/commit/3d6b51886a46119641b712f3d0808d3e171d8a78))
* **ui:** desktop menu, touchpad zoom, filter-circle setting, and a quieter APRS-IS pill ([20f727f](https://github.com/APRS-NET/AprsMap/commit/20f727f9b1226bb7cb7b3e3a9dc8ba24ef6be874))
* **ui:** Show the filter's ranges on the map is remembered ([6439853](https://github.com/APRS-NET/AprsMap/commit/6439853ea50ff70abc632137bc84014e6b160808))
* **ui:** the app menu opens filled on desktop, and a touchpad zooms the map ([7f442b9](https://github.com/APRS-NET/AprsMap/commit/7f442b9a7ea00b4d20ec583bcaf627fa5b545bea))

## [0.2.0-alpha.3](https://github.com/APRS-NET/AprsMap/compare/v0.2.0-alpha.2...v0.2.0-alpha.3) (2026-10-16)


### Features

* **engine:** build the APRS-IS filter from my range, map views and followed stations ([ef2c324](https://github.com/APRS-NET/AprsMap/commit/ef2c32466de5370bc5be80c6141d0baf61277f7f))
* **engine:** build the APRS-IS filter from my range, map views and followed stations ([b802d2c](https://github.com/APRS-NET/AprsMap/commit/b802d2cf82e221a39f0ff331933fd8074086255d))
* **engine:** distance and bearing from my location ([51a3442](https://github.com/APRS-NET/AprsMap/commit/51a344260ce045d9755ae210c86ec9c52015e1b2))
* **engine:** distance and bearing from my location ([5ad0c58](https://github.com/APRS-NET/AprsMap/commit/5ad0c586c92619fe3a9b5cce1858b03f2fe433f8))
* **engine:** query the station list a page at a time ([f09887d](https://github.com/APRS-NET/AprsMap/commit/f09887d06c095c87eda61b2c23014431bb27b731))
* **engine:** several sources at once, with the source of every packet and where else it was seen ([1ce329e](https://github.com/APRS-NET/AprsMap/commit/1ce329e70dc32166164ee6f59756e220f0a17454))
* **engine:** several sources at once, with the source of every packet and where else it was seen ([b1079c2](https://github.com/APRS-NET/AprsMap/commit/b1079c2502f3acd412dbc6b0b4b9e76118f1db19))
* **engine:** sources in the settings, with profiles picking from them ([6a7221d](https://github.com/APRS-NET/AprsMap/commit/6a7221de9d5e67e3ff0663bbfdb4633cb69f95e8))
* **engine:** sources in the settings, with profiles picking from them ([c8e2ab9](https://github.com/APRS-NET/AprsMap/commit/c8e2ab9c040cf5ad3e855df17f30c65dc62ec306))
* **engine:** station details say what sent the last packet and how it arrived ([8612cad](https://github.com/APRS-NET/AprsMap/commit/8612cad8e423d233fc45e8db5f7355390826ead8))
* **gateway:** run behind a proxy, with security headers and caching ([3f7b0e0](https://github.com/APRS-NET/AprsMap/commit/3f7b0e07cff3d1d7fb91198d0b4b6fa6f2d14ff8))
* **gateway:** run behind a proxy, with security headers and caching ([90cc78c](https://github.com/APRS-NET/AprsMap/commit/90cc78ca0e382e83cc1654727834b9a667f519a5))
* **gateway:** say when the gateway is reconnecting, and when the page is out of date ([cbee88b](https://github.com/APRS-NET/AprsMap/commit/cbee88b359393e820cf7f5bc2e8ca93db091b109))
* **gateway:** say when the gateway is reconnecting, and when the page is out of date ([c3673cf](https://github.com/APRS-NET/AprsMap/commit/c3673cf93a2323c6e594a1fd4400890e34a26979))
* **gateway:** viewer limits, rate limiting and the viewer count ([ea80886](https://github.com/APRS-NET/AprsMap/commit/ea808861cd091aa6474dc5574ff79da03ce4be64))
* **gateway:** viewer limits, rate limiting and the viewer count ([dc3b10b](https://github.com/APRS-NET/AprsMap/commit/dc3b10b298e9d200ba4622d17f8250115c55c684))
* **map:** my location as a house, and Set my location here with Undo ([b3a74a7](https://github.com/APRS-NET/AprsMap/commit/b3a74a739bc604b8317c34c89cec8737ad7f8e58))
* **packets:** direct, digipeated and duplicate markers; duplicates don't update stations ([#95](https://github.com/APRS-NET/AprsMap/issues/95)) ([a2ba9c2](https://github.com/APRS-NET/AprsMap/commit/a2ba9c29645b910492b844e1e3944dc90dcae499))
* **ui:** report the map for the filter, set the range, and show the filter ([fadc82f](https://github.com/APRS-NET/AprsMap/commit/fadc82f76fc542d12a5f7340be57bd32786344bd))
* **ui:** report the map for the filter, set the range, and show the filter ([94dd44b](https://github.com/APRS-NET/AprsMap/commit/94dd44be767f9502dc31b0a823dd709f9c3cf43f))
* **ui:** Settings &gt; Sources, and profiles picking their source ([6097399](https://github.com/APRS-NET/AprsMap/commit/609739929153b82dadb4205b9b56078f6127aa75))
* **ui:** the Details panel ([e4e1317](https://github.com/APRS-NET/AprsMap/commit/e4e131747092f668dc5c89fc6fb1b933e32a68db))
* **ui:** the Stations panel ([891d0fb](https://github.com/APRS-NET/AprsMap/commit/891d0fb149479830ef64418918747396f9997463))


### Bug Fixes

* **engine:** a generic destination names no device ([2562e06](https://github.com/APRS-NET/AprsMap/commit/2562e06abf314ddb248acaeb7c79b7df07ff70f6))
* **ui:** source choices fit at double text size ([c8a1ac6](https://github.com/APRS-NET/AprsMap/commit/c8a1ac67f552d450d7a617a3d9c54f35ddb89694))

## [0.2.0-alpha.2](https://github.com/APRS-NET/AprsMap/compare/v0.2.0-alpha.1...v0.2.0-alpha.2) (2026-10-15)


### Features

* **core:** a station store with objects, aging and a cap ([f71dba1](https://github.com/APRS-NET/AprsMap/commit/f71dba1cd74d9684d1bb6adffb246e281c8f21b6))
* **core:** a station store with objects, aging and a cap ([fa460d0](https://github.com/APRS-NET/AprsMap/commit/fa460d077d440778f7d3ca707ea69d52759cb35c))
* **core:** say why APRS-IS is down, stop retrying refused logins, reconnect on request ([7fc36d1](https://github.com/APRS-NET/AprsMap/commit/7fc36d17ba41c0f3f579348b3848c9aa4618ddc7))
* **engine:** packet header, kind, parse issues and a filtered packet stream ([05e0956](https://github.com/APRS-NET/AprsMap/commit/05e0956413ad46c56b7cc9dc368826ad54e1f462))
* **tiles:** a policy-following tile cache, in process and behind the gateway ([de20f29](https://github.com/APRS-NET/AprsMap/commit/de20f29778174b7ee1cbf769b60a794599da72c3))
* **ui:** dockable panels around the map, with layouts that persist and stay on screen ([b44b23c](https://github.com/APRS-NET/AprsMap/commit/b44b23cae29a36b5c1ec244715b5733579b9157e))
* **ui:** dockable panels around the map, with layouts that persist and stay on screen ([9a6e480](https://github.com/APRS-NET/AprsMap/commit/9a6e480e785992d8c8e6a1a143ec2fa0667b3bca))
* **ui:** move, show and gather panels from menus; Ctrl+Tab; narrow-window tabs ([1e74570](https://github.com/APRS-NET/AprsMap/commit/1e74570d701dfcb7201d036097739a4fc027fba0))
* **ui:** move, show and gather panels from menus; Ctrl+Tab; narrow-window tabs ([4b709c7](https://github.com/APRS-NET/AprsMap/commit/4b709c78b5c7b030e09048184e3fb9816af9857f))
* **ui:** Settings › Map ([5c1e412](https://github.com/APRS-NET/AprsMap/commit/5c1e412fe6e860d19cc5391a33352becfdc69a78))
* **ui:** Settings › Map ([22787fc](https://github.com/APRS-NET/AprsMap/commit/22787fcd990cec4a5245c4dd21c4ad962f66eae8))
* **ui:** start over from Settings › Data, or with --reset-all ([4930c77](https://github.com/APRS-NET/AprsMap/commit/4930c77980bad62d3c62a88923742134040aade5))
* **ui:** start over from Settings › Data, or with --reset-all ([db74d8b](https://github.com/APRS-NET/AprsMap/commit/db74d8b4ee859737e61547259319862ec354008e)), closes [#81](https://github.com/APRS-NET/AprsMap/issues/81)
* **ui:** stations on the map, with symbols, labels and selection ([e1296c6](https://github.com/APRS-NET/AprsMap/commit/e1296c6b74a752bdea797e1bd29061af0343baf7))
* **ui:** the map, with tiles through the app's cache ([0085aca](https://github.com/APRS-NET/AprsMap/commit/0085aca88c73d81fbef4d404de79ab3af464b965))
* **ui:** the Packets panel with filters, pause, parse issues and save ([ced31c6](https://github.com/APRS-NET/AprsMap/commit/ced31c636171e36d5b795e3077c7b584bca93bbd))
* **ui:** the Packets panel with filters, pause, parse issues and save ([5eabc68](https://github.com/APRS-NET/AprsMap/commit/5eabc68ec89760252de442c29d926eea7a6bdf64))


### Bug Fixes

* **map:** tolerate a tile landing while the tile layer is disposed ([87ce2c8](https://github.com/APRS-NET/AprsMap/commit/87ce2c8490e60244551db7ce937b3d910c322ba9))
* **ui:** use English plural rules when no locale is set ([8cddedd](https://github.com/APRS-NET/AprsMap/commit/8cddedd3322bf723a73c21849b7144ae800da4fb))


### Performance Improvements

* **map:** hold 60 fps with 2,000 stations in the browser ([5af696e](https://github.com/APRS-NET/AprsMap/commit/5af696e492057b7ec6ca58cae27b409a165c45df))

## [0.2.0-alpha.1](https://github.com/APRS-NET/AprsMap/compare/v0.1.0-alpha.10...v0.2.0-alpha.1) (2026-10-15)


### Features

* **core:** run the APRS-IS session on APRS.NET's AprsIsConnection ([074f9e3](https://github.com/APRS-NET/AprsMap/commit/074f9e3c70347cbaac544139f906358290a3fa04))
* **core:** run the APRS-IS session on APRS.NET's AprsIsConnection ([38e92f4](https://github.com/APRS-NET/AprsMap/commit/38e92f4e99b78f5007bab7a57f7864b6d2f758b3))
* **ui:** accessibility checks, touch targets and 200 % layout fixes ([fb1aae7](https://github.com/APRS-NET/AprsMap/commit/fb1aae7c48f8bea1975aa05885d81f3073521614))
* **ui:** accessibility checks, touch targets and 200 % layout fixes ([f3c55bc](https://github.com/APRS-NET/AprsMap/commit/f3c55bc0feeb5baad7aca02ffff248253e1de759))
* **ui:** appearance palettes and per-viewer preferences ([086bb62](https://github.com/APRS-NET/AprsMap/commit/086bb62e984168268801d54f73d1b156da373c69))
* **ui:** appearance palettes and per-viewer preferences ([1818e3c](https://github.com/APRS-NET/AprsMap/commit/1818e3c547ed2a51b21c64126f9d0c26b6deeadf))
* **ui:** manage station profiles in Settings ([641294e](https://github.com/APRS-NET/AprsMap/commit/641294e4144df7ea02e36a2da23657da09df4631))
* **ui:** manage station profiles in Settings ([de2a838](https://github.com/APRS-NET/AprsMap/commit/de2a838a3c815b89ce1a3d18f7c9ac1650bcdc59))
* **ui:** Settings screen with Appearance and About ([5884314](https://github.com/APRS-NET/AprsMap/commit/58843147d952e34f17499f2f2811a18e0f30c869))
* **ui:** Settings screen with Appearance and About ([e86ba03](https://github.com/APRS-NET/AprsMap/commit/e86ba03a4f5f642f6ce058de25fbe9541d6395cc))


### Bug Fixes

* **desktop:** pass the pseudo-language setting's value, not the configuration ([aa3b209](https://github.com/APRS-NET/AprsMap/commit/aa3b209e38c65d67bef563c39d1f02a2a5feaf8a))
* **hosting:** export APRS.NET's metrics and traces ([a25fd47](https://github.com/APRS-NET/AprsMap/commit/a25fd47f1d107bba5334b1f7375facd29edbaeb6))
* **hosting:** export APRS.NET's metrics and traces ([052e44a](https://github.com/APRS-NET/AprsMap/commit/052e44a38f68af373f424df10cff33c3292e8dec))


### Documentation

* list APRS.NET 0.2.0's metrics in the README ([b355c2d](https://github.com/APRS-NET/AprsMap/commit/b355c2dad64510e6880cf3a42b1dd13ba99c8746))

## [0.1.0-alpha.10](https://github.com/APRS-NET/AprsMap/compare/v0.1.0-alpha.9...v0.1.0-alpha.10) (2026-10-15)


### Features

* use the APRS.NET icon for the desktop and web apps ([f4f0a88](https://github.com/APRS-NET/AprsMap/commit/f4f0a88f2f4fa6ae01b4c1c967c6c5a23bc09256))

## [0.1.0-alpha.9](https://github.com/APRS-NET/AprsMap/compare/v0.1.0-alpha.8...v0.1.0-alpha.9) (2026-10-15)


### Features

* **ui:** this device's location in the setup wizard ([cc4dc0d](https://github.com/APRS-NET/AprsMap/commit/cc4dc0d6b9bbe8ebe86531341c451eef34adc8cf))
* **ui:** this device's location in the setup wizard ([c39c119](https://github.com/APRS-NET/AprsMap/commit/c39c119dec67729038c6c2d6dcfdc8988a4b6bfa))


### Bug Fixes

* **browser:** explain a failed start instead of staying on "Loading…" ([ab5dbb4](https://github.com/APRS-NET/AprsMap/commit/ab5dbb48c0f32e05085fd96e63a16b48dfc2b447))

## [0.1.0-alpha.8](https://github.com/APRS-NET/AprsMap/compare/v0.1.0-alpha.7...v0.1.0-alpha.8) (2026-10-15)


### Features

* **ui:** first-run setup wizard ([0e448ca](https://github.com/APRS-NET/AprsMap/commit/0e448caa9140034bfdccae74f091e450e6677659))
* **ui:** first-run setup wizard ([4e1bea3](https://github.com/APRS-NET/AprsMap/commit/4e1bea319a165d8a304f1604f5c730569fb758b5))


### Bug Fixes

* **engine:** log in with a range filter when a profile has none ([4abceac](https://github.com/APRS-NET/AprsMap/commit/4abceac9ea30f583e1679dd94792073c0be3a42d))
* **engine:** log in with a range filter when a profile has none ([61f8fd0](https://github.com/APRS-NET/AprsMap/commit/61f8fd0dda8db6285170606b8b4548fb4e5fd018))

## [0.1.0-alpha.7](https://github.com/APRS-NET/AprsMap/compare/v0.1.0-alpha.6...v0.1.0-alpha.7) (2026-10-15)


### Features

* **engine:** server presets and Maidenhead locators for the setup wizard ([79d0789](https://github.com/APRS-NET/AprsMap/commit/79d0789f53ed5099739fb62caaf53c6ee47ba1a3))
* **engine:** server presets and Maidenhead locators for the setup wizard ([14205e5](https://github.com/APRS-NET/AprsMap/commit/14205e5c89219e15f94ab7e84b0be07d6b132cfc))

## [0.1.0-alpha.6](https://github.com/APRS-NET/AprsMap/compare/v0.1.0-alpha.5...v0.1.0-alpha.6) (2026-10-15)


### Features

* **engine:** report errors as codes the UI can translate ([582c6c6](https://github.com/APRS-NET/AprsMap/commit/582c6c64b40105d26be9e5f6dfb2243ff722f664))
* **engine:** report errors as codes the UI can translate ([96f62d9](https://github.com/APRS-NET/AprsMap/commit/96f62d90399ba897c27f5a54e8049ed2e4e935eb))
* **ui:** move UI text to resources with a live language switch ([7090e00](https://github.com/APRS-NET/AprsMap/commit/7090e00d03f25a356e1fd554baccf74f8f437286))
* **ui:** move UI text to resources with a live language switch ([6cdcd56](https://github.com/APRS-NET/AprsMap/commit/6cdcd563252abc62e0b9931e90a0c9de59745a02))

## [0.1.0-alpha.5](https://github.com/APRS-NET/AprsMap/compare/v0.1.0-alpha.4...v0.1.0-alpha.5) (2026-10-14)


### Features

* **engine:** address lookup and passcode calculator for the setup wizard ([3d4a0a8](https://github.com/APRS-NET/AprsMap/commit/3d4a0a8a73492fd55c77e7aebfd73061317197df))
* **engine:** address lookup and passcode calculator for the setup wizard ([b95a066](https://github.com/APRS-NET/AprsMap/commit/b95a066d40425d9b1cc665c3679b075ea63b62e8))

## [0.1.0-alpha.4](https://github.com/APRS-NET/AprsMap/compare/v0.1.0-alpha.3...v0.1.0-alpha.4) (2026-10-14)


### Features

* **settings:** per-user settings with station profiles and switching ([8830c21](https://github.com/APRS-NET/AprsMap/commit/8830c21229667b6f76aa73b74380d122ef0546cc))
* **settings:** per-user settings with station profiles and switching ([f11f587](https://github.com/APRS-NET/AprsMap/commit/f11f587ec7959b208b5ecda726db38684606c0f8))

## [0.1.0-alpha.3](https://github.com/APRS-NET/AprsMap/compare/v0.1.0-alpha.2...v0.1.0-alpha.3) (2026-10-14)


### Features

* **engine:** stream the map's stations and survive gateway restarts ([b666b6d](https://github.com/APRS-NET/AprsMap/commit/b666b6dd6ea16e071c62b8e47a90d64ad1a6002a))
* **engine:** stream the map's stations and survive gateway restarts ([823aabd](https://github.com/APRS-NET/AprsMap/commit/823aabd8b5451af49f107e79c3bdd9a892edc034))

## [0.1.0-alpha.2](https://github.com/APRS-NET/AprsMap/compare/v0.1.0-alpha.1...v0.1.0-alpha.2) (2026-10-14)


### Bug Fixes

* **web:** give the SignalR hub source-generated JSON metadata ([7d2f169](https://github.com/APRS-NET/AprsMap/commit/7d2f169929b1ca10b82d3c3bdbd1709e52f994b3))
* **web:** give the SignalR hub source-generated JSON metadata ([d595d29](https://github.com/APRS-NET/AprsMap/commit/d595d2933cceec240085285afaf0d3c6b6d9d96a))

## 0.1.0-alpha.1 (2026-10-13)


### Features

* scaffold the .NET 10 solution, CI, releases and container ([13aaeb8](https://github.com/APRS-NET/AprsMap/commit/13aaeb86c55804291c6bf62656b04e53a982d705))


### Bug Fixes

* **ci:** package report header and parallel trace test ([87146fd](https://github.com/APRS-NET/AprsMap/commit/87146fdb138678ad0fd0f4b898dff77e76d2650e))


### Documentation

* note where the alpha series starts ([9570ed6](https://github.com/APRS-NET/AprsMap/commit/9570ed6a061e99d558a60876b5e65d7725d25cbc))
