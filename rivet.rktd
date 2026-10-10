#hasheq((name . "brainfuel")
        (display-name . "BrainFuel")
        (version . "0.1.0")
        (build . 1)
        (macos-icon . "shared/assets/brainfuel.icns")
        (identifier . "site.jrtx.brainfuel")
        (release-channel . stable)
        ;; raco rivet release also builds native Linux installers by default
        ;; (deb/rpm/appimage, upstream 2b4388c); BrainFuel ships the signed
        ;; tar.gz only until those formats are adopted deliberately.
        (linux-formats . ())
        (url-schemes . ())
        (file-associations . ())
        (macos-min-version . "14.0")
        (windows-min-version . "10.0.19041.0")
        (backend . "app/backend.rkt")
        (module . "backend")
        (entry . "start")
        (protocol . 1))
