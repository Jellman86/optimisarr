import { mount } from 'svelte'
import './app.css'
import Sidecar from './Sidecar.svelte'

mount(Sidecar, { target: document.getElementById('app')! })
