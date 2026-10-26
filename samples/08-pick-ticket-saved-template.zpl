^FX Pick ticket on a saved template, 4 x 6 inches (812 x 1218 dots at 203 dpi).
^FX Three blocks that make ONE label. Block one draws the empty form (grid lines and captions) and stores the finished
^FX picture in printer memory with the save image command (R:PICKTPL.GRF, N means nothing is printed). Block two recalls
^FX that picture as the background and adds the data of this ticket. Block three deletes the stored form again, so the
^FX sample leaves nothing behind. Shows a Code 128 ticket number.
^FX Sample label from LabelScope. All data is made up.
^XA
^PW812
^LL1218
^LH0,0
^FX Template: outer frame and black header band with reversed text.
^FO8,8^GB796,1202,4^FS
^FO20,20^GB772,90,90,B^FS
^FO44,34^FR^A0N,62,62^FDPICK TICKET^FS
^FX Template: order information grid. Neighbouring boxes share one edge line.
^FO20,122^GB389,84,3^FS
^FO406,122^GB386,84,3^FS
^FO32,130^A0N,22,22^FDORDER^FS
^FO418,130^A0N,22,22^FDCUSTOMER^FS
^FO20,206^GB260,84,3^FS
^FO277,206^GB260,84,3^FS
^FO534,206^GB258,84,3^FS
^FO32,214^A0N,22,22^FDWAVE^FS
^FO289,214^A0N,22,22^FDPICKER^FS
^FO546,214^A0N,22,22^FDZONE^FS
^FX Template: item table with a black header row, one grid line every 70 dots and four columns.
^FO20,302^GB772,50,50,B^FS
^FO32,314^FR^A0N,26,26^FDLOCATION^FS
^FO212,314^FR^A0N,26,26^FDITEM^FS
^FO412,314^FR^A0N,26,26^FDDESCRIPTION^FS
^FO702,314^FR^A0N,26,26^FDQTY^FS
^FO20,352^GB772,560,3^FS
^FO20,422^GB772,3,3^FS
^FO20,492^GB772,3,3^FS
^FO20,562^GB772,3,3^FS
^FO20,632^GB772,3,3^FS
^FO20,702^GB772,3,3^FS
^FO20,772^GB772,3,3^FS
^FO20,842^GB772,3,3^FS
^FO200,352^GB3,560,3^FS
^FO400,352^GB3,560,3^FS
^FO690,352^GB3,560,3^FS
^FX Template: footer box with the captions for the ticket number and the signature.
^FO20,924^GB772,280,3^FS
^FO36,934^A0N,22,22^FDTICKET NO.^FS
^FO620,924^GB3,280,3^FS
^FO636,934^A0N,22,22^FDPICKED BY^FS
^FO636,1150^GB140,3,3^FS
^FX Store the finished picture. The N says do not print this block.
^ISR:PICKTPL.GRF,N
^XZ
^XA
^PW812
^LL1218
^LH0,0
^FX Recall the stored form first, so every data field below lands on top of it.
^ILR:PICKTPL.GRF
^FX Order information.
^FO32,156^A0N,40,40^FDORD-100417^FS
^FO418,156^A0N,36,36^FDSample Store 12^FS
^FO32,240^A0N,40,40^FDW-07^FS
^FO289,240^A0N,40,40^FDJ. Example^FS
^FO546,240^A0N,40,40^FDZONE B^FS
^FX Item lines: location, item number, description and quantity.
^FO32,372^A0N,34,34^FDB-03-02^FS
^FO212,372^A0N,34,34^FDSK-10021^FS
^FO412,372^A0N,30,30^FDCeramic mug, 350 ml^FS
^FO702,367^A0N,40,40^FD12^FS
^FO32,442^A0N,34,34^FDB-03-05^FS
^FO212,442^A0N,34,34^FDSK-10034^FS
^FO412,442^A0N,30,30^FDGlass jar, 500 ml^FS
^FO702,437^A0N,40,40^FD24^FS
^FO32,512^A0N,34,34^FDB-04-01^FS
^FO212,512^A0N,34,34^FDSK-10102^FS
^FO412,512^A0N,30,30^FDWooden spoon set^FS
^FO702,507^A0N,40,40^FD6^FS
^FO32,582^A0N,34,34^FDC-01-04^FS
^FO212,582^A0N,34,34^FDSK-10250^FS
^FO412,582^A0N,30,30^FDLinen napkin, grey^FS
^FO702,577^A0N,40,40^FD48^FS
^FO32,652^A0N,34,34^FDC-02-03^FS
^FO212,652^A0N,34,34^FDSK-10333^FS
^FO412,652^A0N,30,30^FDSteel water bottle^FS
^FO702,647^A0N,40,40^FD8^FS
^FX Ticket number as a Code 128 symbol, with a 10 narrow bar quiet zone on both sides.
^BY4,3,130
^FO64,974^BCN,130,Y,N,N^FDPT100417^FS
^XZ
^XA^IDR:PICKTPL.GRF^FS^XZ
