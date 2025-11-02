L.Control.MousePosition = L.Control.extend({
  options: {
    position: 'bottomleft',
    separator: ' / ',
    emptyString: 'LAT: 0.00000 / LON: 0.00000 / LOC: --',
    numDigits: 5,
    lngFormatter: undefined,
    latFormatter: undefined,
    prefix: ""
  },

  onAdd: function (map) {
    this._container = L.DomUtil.create('div', 'leaflet-control-mouseposition');
    L.DomEvent.disableClickPropagation(this._container);
    map.on('mousemove', this._onMouseMove, this);
    this._container.innerHTML=this.options.emptyString;
    return this._container;
  },

  onRemove: function (map) {
    map.off('mousemove', this._onMouseMove)
  },

  _onMouseMove: function (e) {
    var lng = this.options.lngFormatter ? this.options.lngFormatter(e.latlng.lng) : L.Util.formatNum(e.latlng.lng, this.options.numDigits);
    var lat = this.options.latFormatter ? this.options.latFormatter(e.latlng.lat) : L.Util.formatNum(e.latlng.lat, this.options.numDigits);

	var ydiv_arr=new Array(10, 1, 1/24, 1/240, 1/240/24);
	var d1 = "ABCDEFGHIJKLMNOPQR".split("");
	var d2 = "ABCDEFGHIJKLMNOPQRSTUVWX".split("");
	var d4 = new Array (0, 1, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 3, 4, 4, 4, 5, 5, 5);
	var locator = "";
	var x = lng;
	var y = lat;
	var precision = d4[map.getZoom()];

	while (x < -180) {x += 360;}
	while (x > 180) {x -=360;}
	x = x + 180;
	y = y + 90;

	locator = locator + d1[Math.floor(x/20)] + d1[Math.floor(y/10)];
	for (var i=0; i<4; i=i+1) {
		if (precision > i+1) {
			rlon = x%(ydiv_arr[i]*2);
			rlat = y%(ydiv_arr[i]);
			if ((i%2)==0)
				locator += Math.floor(rlon/(ydiv_arr[i+1]*2)) +""+ Math.floor(rlat/(ydiv_arr[i+1]));
			else
				locator += d2[Math.floor(rlon/(ydiv_arr[i+1]*2))] +""+ d2[Math.floor(rlat/(ydiv_arr[i+1]))];
		}
	}

    var value = 'LAT: '+ lat + this.options.separator +'LON: '+ lng  + this.options.separator +' LOC: '+ locator;
    var prefixAndValue = this.options.prefix + ' ' + value;
    this._container.innerHTML = prefixAndValue;
  }

});

L.Map.mergeOptions({
    positionControl: false
});

L.Map.addInitHook(function () {
    if (this.options.positionControl) {
        this.positionControl = new L.Control.MousePosition();
        this.addControl(this.positionControl);
    }
});

L.control.mousePosition = function (options) {
    return new L.Control.MousePosition(options);
};