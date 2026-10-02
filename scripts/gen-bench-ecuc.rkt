#lang racket/base

;; Synthetic large ECUC workspace generator for the R0 stop-rule benchmark.
;; Writes ~1.1M lines of realistic ECUC ARXML (module configuration values
;; with containers, parameters, references) into a temp directory.

(require racket/cmdline racket/file)

(define (write-module path index container-count)
  (call-with-output-file path
    (λ (out)
      (displayln "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" out)
      (displayln "<AUTOSAR xmlns=\"http://autosar.org/schema/r4.0\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:schemaLocation=\"http://autosar.org/schema/r4.0 AUTOSAR_4-3-0.xsd\">" out)
      (displayln "  <AR-PACKAGES>" out)
      (fprintf out "    <AR-PACKAGE>\n      <SHORT-NAME>Pkg~a</SHORT-NAME>\n      <ELEMENTS>\n" index)
      (fprintf out "        <ECUC-MODULE-CONFIGURATION-VALUES>\n          <SHORT-NAME>Mcu~a</SHORT-NAME>\n          <DEFINITION-REF>/Vendor/Mcu</DEFINITION-REF>\n          <CONTAINERS>\n" index)
      (for ([i (in-range container-count)])
        (fprintf out "            <ECUC-CONTAINER-VALUE>\n              <SHORT-NAME>Container~a</SHORT-NAME>\n              <DEFINITION-REF>/Vendor/Mcu/McuGeneralConfiguration/ContainerDef</DEFINITION-REF>\n              <PARAMETER-VALUES>\n" i)
        (fprintf out "                <ECUC-NUMERICAL-PARAM-VALUE>\n                  <DEFINITION-REF>/Vendor/Mcu/McuGeneralConfiguration/Container~a/McuFrequency</DEFINITION-REF>\n                  <VALUE>80000000</VALUE>\n                </ECUC-NUMERICAL-PARAM-VALUE>\n" i)
        (fprintf out "                <ECUC-TEXTUAL-PARAM-VALUE>\n                  <DEFINITION-REF>/Vendor/Mcu/McuGeneralConfiguration/Container~a/McuClockReference</DEFINITION-REF>\n                  <VALUE>McuClockReferencePoint</VALUE>\n                </ECUC-TEXTUAL-PARAM-VALUE>\n" i)
        (fprintf out "                <ECUC-NUMERICAL-PARAM-VALUE>\n                  <DEFINITION-REF>/Vendor/Mcu/McuGeneralConfiguration/Container~a/McuTimeout</DEFINITION-REF>\n                  <VALUE>~a</VALUE>\n                </ECUC-NUMERICAL-PARAM-VALUE>\n" i (+ i 1000))
        (fprintf out "              </PARAMETER-VALUES>\n              <REFERENCE-VALUES>\n                <ECUC-REFERENCE-VALUE>\n                  <DEFINITION-REF>/Vendor/Mcu/McuGeneralConfiguration/Container~a/NextContainerRef</DEFINITION-REF>\n                  <VALUE-REF>/Pkg~a/Mcu~a/Container~a</VALUE-REF>\n                </ECUC-REFERENCE-VALUE>\n              </REFERENCE-VALUES>\n            </ECUC-CONTAINER-VALUE>\n" i index index (if (= i (sub1 container-count)) 0 (add1 i))))
      (displayln "          </CONTAINERS>" out)
      (displayln "        </ECUC-MODULE-CONFIGURATION-VALUES>" out)
      (displayln "      </ELEMENTS>" out)
      (displayln "    </AR-PACKAGE>" out)
      (displayln "  </AR-PACKAGES>" out)
      (displayln "</AUTOSAR>" out))
    #:exists 'replace))

(module+ main
  (define out-dir (make-parameter "."))
  (define container-count (make-parameter 50000))
  (command-line
   #:program "gen-bench-ecuc"
   #:once-each
   [("-o" "--out") d "output directory" (out-dir d)]
   [("-n" "--containers") n "containers per module" (container-count (string->number n))]
   #:args ()
   (make-directory* (out-dir))
   (write-module (build-path (out-dir) "mcu.arxml") 0 (container-count))
   (printf "written: ~a containers\n" (container-count))))
